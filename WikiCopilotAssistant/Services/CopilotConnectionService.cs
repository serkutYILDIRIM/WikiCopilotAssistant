using System.ComponentModel;
using System.Text.Json;
using GitHub.Copilot;
using WikiCopilotAssistant.Models;

namespace WikiCopilotAssistant.Services;

public sealed class CopilotConnectionService(ILogger<CopilotConnectionService> logger) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object leaseLock = new();
    private int leases;
    private TaskCompletionSource leasesReleased = CompletedSignal();
    private CopilotClient? client;
    private DirectoryInfo? workingDirectory;
    private CopilotStatus status = new("checking", "Copilot bağlantısı kontrol ediliyor.");

    public CopilotStatus Status => Volatile.Read(ref status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
    }

    public async Task<CopilotStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        if (!await gate.WaitAsync(0, deadline.Token))
            return Status;
        try
        {
            lock (leaseLock)
            {
                // Research owns the runtime until its session and source cleanup have finished.
                if (leases != 0)
                    return Status;
            }
            var previous = Status;
            SetStatus("checking", "Copilot bağlantısı kontrol ediliyor.");
            // A fresh runtime observes a login completed in the user's own terminal.
            if (!previous.IsReady && client is not null)
            {
                var oldClient = client;
                client = null;
                await oldClient.DisposeAsync();
            }

            workingDirectory ??= Directory.CreateTempSubdirectory("wiki-copilot-web-");
            client ??= CopilotRuntime.CreateClient(workingDirectory.FullName);
            await client.StartAsync(deadline.Token);
            var auth = await client.GetAuthStatusAsync(deadline.Token);
            if (!auth.IsAuthenticated)
                return SetStatus("sign_in_required", "Copilot oturumu bulunamadı. Kendi terminalinizde giriş yapın.");

            var models = await client.ListModelsAsync(deadline.Token);
            return models.Count == 0
                ? SetStatus("no_models", "Hesabınız için kullanılabilir model bulunamadı. Copilot erişiminizi ve kurum politikanızı kontrol edin.")
                : SetStatus("ready", "Copilot bağlantısı hazır. Giriş bilgileri uygulamada saklanmaz.");
        }
        catch (ArgumentException error) when (error.ParamName == "cliPath")
        {
            return SetStatus("unavailable", "Yapılandırılan Copilot CLI yolu geçersiz. Yerel CLI ayarını kontrol edin.");
        }
        catch (FileNotFoundException)
        {
            return SetStatus("unavailable", "Copilot runtime dosyası bulunamadı. README içindeki onaylı kurulum adımlarını izleyin; otomatik indirme yapılmadı.");
        }
        catch (InvalidOperationException error) when (
            error.Message.StartsWith("Copilot runtime wrapper not found at ", StringComparison.Ordinal))
        {
            return SetStatus("unavailable", "Copilot runtime derleme çıktısında eksik. Mevcut runtime ile yeniden derleyin; otomatik indirme yapılmadı.");
        }
        catch (JsonException)
        {
            return SetStatus("unavailable", "Copilot CLI ile SDK yanıt biçimleri uyumlu değil. README içindeki sürüm bilgilerini kontrol edin.");
        }
        catch (HttpRequestException)
        {
            return SetStatus("unavailable", "Copilot hizmetine erişilemedi. İnternet bağlantınızı kontrol edip yeniden deneyin.");
        }
        // SDK 1.0.13 exposes RPC failures using an internal exception type.
        catch (Exception error) when (error.GetType().FullName == "GitHub.Copilot.RemoteRpcException")
        {
            return SetStatus("unavailable", "Copilot isteği tamamlayamadı. Hesap erişiminizi ve kurum politikanızı kontrol edip yeniden deneyin.");
        }
        catch (IOException)
        {
            return SetStatus("unavailable", "Copilot runtime bağlantısı kesildi. Bağlantıyı yeniden kontrol edin.");
        }
        catch (Win32Exception)
        {
            return SetStatus("unavailable", "Copilot runtime başlatılamadı. Yerel kurulum ve çalıştırma izinlerini kontrol edin.");
        }
        catch (TimeoutException)
        {
            return SetStatus("unavailable", "Copilot bağlantı kontrolü zaman aşımına uğradı.");
        }
        catch (OperationCanceledException)
        {
            return SetStatus("unavailable", "Copilot bağlantı kontrolü iptal edildi veya zaman aşımına uğradı.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ClientLease?> AcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (shutdown.IsCancellationRequested || !Status.IsReady || client is null || workingDirectory is null)
                return null;
            lock (leaseLock)
            {
                if (leases++ == 0)
                    leasesReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return new ClientLease(this, client, workingDirectory.FullName);
        }
        finally
        {
            gate.Release();
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    public sealed class ClientLease : IDisposable
    {
        private readonly CopilotConnectionService owner;
        private int disposed;
        internal ClientLease(CopilotConnectionService owner, CopilotClient client, string directory)
        {
            this.owner = owner;
            Client = client;
            WorkingDirectory = directory;
        }
        public CopilotClient Client { get; }
        public string WorkingDirectory { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            lock (owner.leaseLock)
            {
                if (--owner.leases == 0)
                    owner.leasesReleased.TrySetResult();
            }
        }
    }

    private CopilotStatus SetStatus(string state, string message)
    {
        var next = new CopilotStatus(state, message);
        Volatile.Write(ref status, next);
        if (state == "unavailable")
            logger.LogWarning("Copilot connection is unavailable; see the safe status message in the local UI.");
        return next;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await shutdown.CancelAsync();
        await base.StopAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            Task released;
            lock (leaseLock)
                released = leasesReleased.Task;
            await released.WaitAsync(cancellationToken);
            if (client is not null)
            {
                await client.DisposeAsync();
                client = null;
            }
            workingDirectory?.Delete(recursive: true);
            workingDirectory = null;
        }
        finally
        {
            gate.Release();
        }
    }

    public override void Dispose()
    {
        shutdown.Cancel();
        base.Dispose();
    }
}
