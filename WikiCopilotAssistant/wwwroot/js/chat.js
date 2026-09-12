"use strict";

const statusMessage = document.getElementById("connection-message");
const dialog = document.getElementById("login-dialog");
const sourceForm = document.getElementById("source-form");
const startButton = document.getElementById("prepare-button");
const refreshButtons = document.querySelectorAll("[data-refresh]");
const stopButton = document.getElementById("stop-research");
const approvalCard = document.getElementById("host-approval");
const researchError = document.getElementById("research-error");
let ready = statusMessage.dataset.state === "ready";
let checking = false;
let loginShown = false;
let connectionTimer;
let researchTimer;
let researchBusy = false;
let researchLoaded = false;
let actionPending = false;
let currentResearch = null;
let generation = 0;
let previousSources = "";
let pollingFailed = false;

function showLogin() {
    if (!dialog.open) dialog.showModal();
}

function updateControls() {
    const active = currentResearch?.isActive === true;
    startButton.disabled = !ready || checking || !researchLoaded || active || actionPending;
    sourceForm.querySelectorAll("input:not([type=hidden]), textarea").forEach(input => {
        input.disabled = active || actionPending;
    });
    stopButton.hidden = !active;
    stopButton.disabled = actionPending || currentResearch?.state === "cancelled";
    refreshButtons.forEach(button => button.disabled = checking || active || actionPending);
    document.querySelectorAll("#host-approval button").forEach(button => button.disabled = actionPending);
}

function applyConnection(status) {
    ready = status.isReady === true;
    statusMessage.textContent = status.message;
    statusMessage.dataset.state = status.state;
    document.getElementById("dialog-status").textContent = status.message;
    document.getElementById("status-dot").classList.toggle("ready", ready);
    if (status.state === "sign_in_required" && !loginShown) {
        loginShown = true;
        showLogin();
    }
    if (status.state === "checking") {
        clearTimeout(connectionTimer);
        connectionTimer = setTimeout(() => checkConnection(false), 1500);
    }
    updateControls();
}

async function requestJson(url, method = "GET", fields) {
    const options = { method, cache: "no-store", credentials: "same-origin" };
    if (method === "POST") {
        options.headers = {
            "X-CSRF-TOKEN": sourceForm.querySelector('input[name="__RequestVerificationToken"]').value
        };
        options.body = fields instanceof FormData ? fields : new URLSearchParams(fields);
    }
    const response = await fetch(url, options);
    const isJson = response.headers.get("content-type")?.includes("application/json");
    const payload = isJson ? await response.json() : null;
    if (!response.ok) {
        throw new Error(payload?.message ||
            (response.status === 400 ? "İstek doğrulanamadı. Sayfayı yenileyip tekrar deneyin." :
                "İşlem tamamlanamadı. Uygulama bağlantısını kontrol edip yeniden deneyin."));
    }
    if (!payload) throw new Error("Uygulama beklenen yanıtı vermedi. Sayfayı yenileyin.");
    return payload;
}

async function checkConnection(refresh) {
    if (checking || currentResearch?.isActive) return;
    checking = true;
    clearTimeout(connectionTimer);
    updateControls();
    if (refresh) {
        statusMessage.textContent = "Copilot bağlantısı yeniden kontrol ediliyor…";
        document.getElementById("dialog-status").textContent = statusMessage.textContent;
    }
    try {
        applyConnection(await requestJson(refresh ? "/copilot/refresh" : "/copilot/status", refresh ? "POST" : "GET"));
    } catch (error) {
        applyConnection({ state: "unavailable", isReady: false, message: error.message });
    } finally {
        checking = false;
        updateControls();
    }
}

function showResearchError(message) {
    researchError.textContent = message;
    researchError.hidden = !message;
}

function renderSources(sources) {
    const signature = JSON.stringify(sources);
    if (signature === previousSources) return;
    previousSources = signature;
    const container = document.getElementById("source-cards");
    container.replaceChildren();
    for (const source of sources) {
        const card = document.createElement("article");
        card.className = "source-result";
        const heading = document.createElement("h4");
        const link = document.createElement("a");
        let url;
        try { url = new URL(source.url); } catch { url = null; }
        if (url && ["http:", "https:"].includes(url.protocol) && !url.username && !url.password) {
            link.href = url.href;
            link.target = "_blank";
            link.rel = "noopener noreferrer";
        }
        link.textContent = source.title || source.url;
        heading.append(link);
        const address = document.createElement("p");
        address.className = "source-address";
        address.textContent = source.url;
        const text = document.createElement("p");
        text.textContent = source.text || "";
        const attribution = document.createElement("p");
        attribution.className = "field-hint";
        attribution.textContent = [
            source.id, source.method, source.author, source.license,
            source.truncated ? "Kaynak kısaltılmıştır" : null
        ].filter(Boolean).join(" · ");
        card.append(heading, address, text, attribution);
        container.append(card);
    }
    document.getElementById("no-sources").hidden = sources.length > 0;
}

function renderResearch(snapshot) {
    currentResearch = snapshot.state === "idle" ? null : snapshot;
    researchLoaded = true;
    const panel = document.getElementById("research-panel");
    panel.hidden = !currentResearch && researchError.hidden;
    if (currentResearch) {
        const headings = {
            running: "Kaynaklar inceleniyor",
            awaiting_approval: "Site erişimi için onay bekleniyor",
            completed: "Araştırma tamamlandı",
            cancelled: "Araştırma durduruldu",
            timed_out: "Araştırma zaman aşımına uğradı",
            failed: "Araştırma tamamlanamadı"
        };
        document.getElementById("research-title").textContent = headings[snapshot.state] || "Araştırma";
        document.getElementById("research-message").textContent = snapshot.message +
            (snapshot.isActive && ["completed", "cancelled", "timed_out", "failed"].includes(snapshot.state)
                ? " Oturum temizliği sürüyor; bitince yeni araştırma başlatabilirsiniz." : "");
        document.getElementById("research-count").textContent = `${snapshot.toolCalls} araç çağrısı · Sabit çağrı sınırı yok`;
        document.getElementById("approved-hosts").textContent =
            `İzin verilen siteler: ${(snapshot.approvedHosts || []).join(", ")}`;
        const approval = snapshot.approval;
        approvalCard.hidden = !approval;
        if (approval) {
            document.getElementById("approval-host").textContent = approval.host;
            document.getElementById("approval-message").textContent = approval.message;
        }
        document.getElementById("research-answer").hidden = !snapshot.answer;
        document.getElementById("answer-text").textContent = snapshot.answer || "";
        renderSources(snapshot.sources || []);
    } else {
        approvalCard.hidden = true;
        document.getElementById("research-title").textContent = "Araştırma";
        document.getElementById("research-message").textContent = "";
        document.getElementById("research-count").textContent = "";
        document.getElementById("approved-hosts").textContent = "";
        document.getElementById("research-answer").hidden = true;
        renderSources([]);
        previousSources = "";
    }
    updateControls();
}

async function pollResearch() {
    if (researchBusy || actionPending) return;
    researchBusy = true;
    const expectedGeneration = generation;
    try {
        const snapshot = await requestJson("/research/status");
        if (expectedGeneration !== generation) return;
        renderResearch(snapshot);
        if (pollingFailed) {
            showResearchError("");
            pollingFailed = false;
        }
    } catch (error) {
        if (expectedGeneration !== generation) return;
        document.getElementById("research-panel").hidden = false;
        pollingFailed = true;
        showResearchError(error.message + " Durum yeniden kontrol edilecek.");
    } finally {
        researchBusy = false;
        clearTimeout(researchTimer);
        researchTimer = setTimeout(pollResearch, currentResearch?.isActive || !researchLoaded ? 1000 : 5000);
    }
}

async function researchAction(url, fields) {
    if (actionPending) return;
    actionPending = true;
    generation++;
    clearTimeout(researchTimer);
    showResearchError("");
    pollingFailed = false;
    updateControls();
    try {
        renderResearch(await requestJson(url, "POST", fields));
    } catch (error) {
        document.getElementById("research-panel").hidden = false;
        showResearchError(error.message);
    } finally {
        actionPending = false;
        updateControls();
        clearTimeout(researchTimer);
        researchTimer = setTimeout(pollResearch, 500);
    }
}

document.querySelectorAll("[data-open-login]").forEach(button => button.addEventListener("click", showLogin));
document.getElementById("close-login").addEventListener("click", () => dialog.close());
dialog.addEventListener("keydown", event => {
    if (event.key === "Escape") {
        event.preventDefault();
        dialog.close();
    }
});
refreshButtons.forEach(button => button.addEventListener("click", () => checkConnection(true)));
sourceForm.addEventListener("submit", async event => {
    event.preventDefault();
    if (!ready || checking || !researchLoaded || currentResearch?.isActive || actionPending) {
        document.getElementById("form-feedback").textContent = "Bağlantı ve araştırma durumu hazır olana kadar bekleyin.";
        return;
    }
    if (!sourceForm.reportValidity()) return;
    const newSource = document.getElementById("SourceUrl").value.trim();
    const previousSource = sourceForm.dataset.previousSource;
    if (previousSource && previousSource !== newSource &&
        !confirm("Kaynak değişti. Mevcut araştırma yeni kaynakla değiştirilsin mi?")) return;
    const fields = new FormData(sourceForm);
    fields.set("SourceUrl", newSource);
    fields.set("Question", document.getElementById("Question").value.trim());
    document.getElementById("form-feedback").textContent = "";
    await researchAction("/research/start", fields);
    if (currentResearch?.isActive) {
        sourceForm.dataset.previousSource = newSource;
        document.getElementById("research-panel").scrollIntoView({ behavior: "smooth", block: "start" });
    }
});
stopButton.addEventListener("click", () => {
    if (currentResearch) researchAction("/research/stop", { id: currentResearch.id });
});
function decideHost(approve) {
    if (currentResearch?.approval) researchAction("/research/approve", {
        id: currentResearch.id, approvalId: currentResearch.approval.id, approve: String(approve)
    });
}
document.getElementById("approve-host").addEventListener("click", () => decideHost(true));
document.getElementById("deny-host").addEventListener("click", () => decideHost(false));
document.getElementById("reset-form").addEventListener("submit", event => {
    if (!confirm("Bu araştırma durdurulup geçici durumu silinsin mi?")) {
        event.preventDefault();
        return;
    }
    generation++;
    clearTimeout(researchTimer);
    event.submitter.disabled = true;
});
window.addEventListener("pageshow", () => {
    checkConnection(false);
    pollResearch();
});
