using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WikiCopilotAssistant.Models;
using WikiCopilotAssistant.Services.Sources;

namespace WikiCopilotAssistant.Services;

public static class AnswerValidator
{
    public const string Instructions = """
        Yalnızca aşağıdaki kesin şemaya uygun tek bir JSON nesnesi döndür:
        {"sourceFacts":[{"text":"Kaynağa dayanan açıklama","citations":[{"sourceId":"S1","quote":"Kaynakta gerçekten bulunan kısa alıntı"}]}],"commentary":["Copilot yorumu / model bilgisi: kaynaklarla doğrulanmamış değerlendirme."],"suggestedSteps":[{"text":"Copilot önerisi: yapılabilecek işlem","citations":[]}],"uncertainties":["Kaynakların desteklemediği veya belirsiz kalan konu."],"similarSources":["S1"]}
        Bu örnekteki S1 ve alıntı yer tutucudur; yalnızca sana verilen gerçek kanıt kayıtlarını kullan.
        Gerçek kaynak kimliğini read_source araç yanıtındaki documents öğesinin SourceId (veya sourceId) alanından al.
        Bu değeri değiştirmeden, büyük/küçük harflerini koruyarak JSON içindeki sourceId alanına yaz.
        Kimliği URL'den, sıralamadan veya başlıktan türetme; kimliği null/eksik olan belgeyi alıntılama veya similarSources içine ekleme.
        Tüm beş üst düzey dizi zorunludur ve null olamaz; kullanılmayan dizileri [] olarak yaz.
        Ek alan, yinelenen JSON anahtarı, yorum, sondaki virgül, Markdown çiti veya JSON dışında metin kullanma.
        Açıklamalar Türkçe olsun; doğrudan alıntıları kaynağın özgün dilinde ve büyük/küçük harflerini koruyarak yaz.
        sourceFacts: her öğe yalnızca text ve citations içerir; en az bir gerçek kaynak alıntısı zorunludur.
        Özetlenmiş gerçekler de alıntı gerektirir. Alıntı kaynağın tam yakalanan metninde kesintisiz bulunmalıdır;
        yalnızca boşluk farklılıkları kabul edilir. Çeviri, üç nokta ekleme veya kelime değiştirme yapma.
        Her citations öğesi yalnızca sourceId ve quote içerir; kaynak kimliği verilen kayıtla tam eşleşmelidir.
        commentary: her öğede bunun Copilot yorumu veya kaynakla doğrulanmamış model bilgisi olduğunu açıkça belirt.
        suggestedSteps: her öğe yalnızca text ve citations içerir; citations:[] bir Copilot önerisidir, kaynak gerçeği değildir.
        Öneriye alıntı eklersen sourceFacts ile aynı alıntı kuralları geçerlidir.
        uncertainties: eksik kanıtları ve belirsizlikleri belirt. Hiç kaynak okunmadıysa bunu açıkça belirt.
        similarSources: yalnızca okunmuş gerçek kaynak kimlikleri; aynı kimliği tekrarlama. Bunlar iddia veya alıntı değildir.
        Metin alanlarında ham http://, https://, www. adresi, Markdown bağlantısı veya otomatik bağlantı kullanma.
        Kaynak kimliklerini yalnızca citations ve similarSources içinde kullan; URL, başlık, yazar, lisans ve external alanları üretme.
        Bağlantılar ve kaynak bilgileri sunucu tarafından eklenir. Gerçek kaynak alıntısının kendi içindeki URL korunabilir.
        text alanları 1–2000 karakter; commentary ve uncertainties öğeleri 1–4000 karakter ve boş olmayan metin olmalıdır.
        quote en fazla 400 karakter ve boşluk normalizasyonundan sonra en az 8 boşluk dışı karakter içermelidir.
        Her dizi en fazla 16 öğe; similarSources en fazla 8 öğe. Bir citations dizisinde aynı kaynak/alıntı çiftini tekrarlama.
        Tüm yanıt boyunca kaynak başına toplam farklı alıntı uzunluğu en fazla 1200 karakterdir.
        Tüm JSON en fazla 64000 karakterdir; uzun içeriği sessizce kesmek yerine kısa alıntı seç.
        Desteklenen gerçek yoksa sourceFacts:[] kullan. Kanıt yoksa tüm citations ve similarSources dizileri [] olmalıdır.
        En az bir sourceFacts, commentary veya suggestedSteps öğesi bulunmalıdır.
        Uygulama yalnızca yapı, gerçek kaynak kimlikleri ve alıntı eşleşmesini denetler; anlamsal doğruluğu kanıtlamaz.
        Bu denetimi doğrulanmış sonuç veya iddianın doğruluğu garantisi olarak sunma.
        """;

    private static readonly Regex ProseLink = new(
        @"\[[^\]\r\n]*\]\s*(?:\(|\[|:)|<\s*(?:[a-z][a-z0-9+.-]*:|www\.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static AnswerValidationResult Validate(
        string json, IReadOnlyDictionary<string, SourceEvidence> evidence)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64_000)
            return new(null, ["Answer JSON must contain 1–64000 characters."]);
        if (evidence is null)
            return new(null, ["The server evidence registry is unavailable."]);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            return new Validation(evidence).Read(document.RootElement);
        }
        catch (JsonException)
        {
            return new(null, ["Return one valid JSON object without fences, comments or trailing content (maximum depth 16)."]);
        }
    }

    private sealed class Validation(IReadOnlyDictionary<string, SourceEvidence> evidence)
    {
        private readonly List<string> errors = [];
        private readonly Dictionary<string, string> normalizedSources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> sourceQuotes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> quoteLengths = new(StringComparer.Ordinal);

        public AnswerValidationResult Read(JsonElement root)
        {
            if (!CheckObject(root, "answer",
                    ["sourceFacts", "commentary", "suggestedSteps", "uncertainties", "similarSources"]))
                return new(null, errors.ToArray());

            var facts = ReadStatements(root.GetProperty("sourceFacts"), "sourceFacts", requireCitations: true);
            var commentary = ReadTexts(root.GetProperty("commentary"), "commentary");
            var steps = ReadStatements(root.GetProperty("suggestedSteps"), "suggestedSteps", requireCitations: false);
            var uncertainties = ReadTexts(root.GetProperty("uncertainties"), "uncertainties");
            var similar = ReadSimilarSources(root.GetProperty("similarSources"));

            if (facts.Count == 0 && commentary.Count == 0 && steps.Count == 0)
                Error("answer", "Include at least one source fact, commentary item or suggested step.");
            if (errors.Count > 0)
                return new(null, errors.ToArray());

            if (evidence.Count == 0)
            {
                const string noSources = "Hiçbir kaynak başarıyla okunmadı; bu yanıt kaynaklarla doğrulanmış değildir.";
                // This is server-owned status, not an additional model-generated array item.
                uncertainties.Add(noSources);
            }
            return new(new ResearchAnswer(
                facts.ToArray(), commentary.ToArray(), steps.ToArray(),
                uncertainties.ToArray(), similar.ToArray()), []);
        }

        private List<AnswerStatement> ReadStatements(JsonElement value, string path, bool requireCitations)
        {
            List<AnswerStatement> statements = [];
            if (!CheckArray(value, path, 16))
                return statements;

            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var itemPath = $"{path}[{index++}]";
                if (!CheckObject(item, itemPath, ["text", "citations"]))
                    continue;

                var text = ReadText(item.GetProperty("text"), $"{itemPath}.text", 2000);
                var citationsElement = item.GetProperty("citations");
                if (!CheckArray(citationsElement, $"{itemPath}.citations", 16))
                    continue;
                if (requireCitations && citationsElement.GetArrayLength() == 0)
                    Error($"{itemPath}.citations", "A source fact requires at least one source citation.");

                List<AnswerCitation> citations = [];
                HashSet<(string SourceId, string Quote)> seen = [];
                var citationIndex = 0;
                foreach (var citation in citationsElement.EnumerateArray())
                {
                    var citationPath = $"{itemPath}.citations[{citationIndex++}]";
                    var parsed = ReadCitation(citation, citationPath);
                    if (parsed is null)
                        continue;
                    if (!seen.Add((parsed.SourceId, NormalizeWhitespace(parsed.Quote))))
                        Error(citationPath, "Do not repeat the same source and quote in a citation array.");
                    citations.Add(parsed);
                }
                if (text is not null)
                    statements.Add(new(text, citations.ToArray()));
            }
            return statements;
        }

        private AnswerCitation? ReadCitation(JsonElement value, string path)
        {
            if (!CheckObject(value, path, ["sourceId", "quote"]))
                return null;
            var source = ResolveSource(value.GetProperty("sourceId"), $"{path}.sourceId");
            var quote = ReadText(value.GetProperty("quote"), $"{path}.quote", 400, allowLinks: true);
            if (source is null || quote is null)
                return null;
            var normalizedQuote = NormalizeWhitespace(quote);
            if (normalizedQuote.Count(character => !char.IsWhiteSpace(character)) < 8)
            {
                Error($"{path}.quote", "Use at least 8 non-whitespace characters from the source.");
                return null;
            }
            if (!normalizedSources.TryGetValue(source.Id, out var sourceText))
            {
                sourceText = NormalizeWhitespace(source.Text);
                normalizedSources.Add(source.Id, sourceText);
            }
            var match = sourceText.IndexOf(normalizedQuote, StringComparison.Ordinal);
            if (match < 0)
            {
                Error($"{path}.quote", "The quote must match the captured source text exactly, ignoring whitespace only.");
                return null;
            }

            var originalQuote = ExtractOriginalQuote(source.Text, match, normalizedQuote);
            if (!sourceQuotes.TryGetValue(source.Id, out var quotes))
            {
                quotes = new(StringComparer.Ordinal);
                sourceQuotes.Add(source.Id, quotes);
                quoteLengths.Add(source.Id, 0);
            }
            if (quotes.Add(normalizedQuote))
            {
                quoteLengths[source.Id] += originalQuote.Length;
                if (quoteLengths[source.Id] > 1200)
                    Error(path, "Total distinct quotations for this source exceed 1200 characters; use shorter quotations.");
            }
            return CreateCitation(source, originalQuote);
        }

        private List<string> ReadTexts(JsonElement value, string path)
        {
            List<string> result = [];
            if (!CheckArray(value, path, 16))
                return result;
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var text = ReadText(item, $"{path}[{index++}]", 4000);
                if (text is not null)
                    result.Add(text);
            }
            return result;
        }

        private List<AnswerCitation> ReadSimilarSources(JsonElement value)
        {
            List<AnswerCitation> result = [];
            if (!CheckArray(value, "similarSources", 8))
                return result;
            HashSet<string> seen = new(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var path = $"similarSources[{index++}]";
                var source = ResolveSource(item, path);
                if (source is null)
                    continue;
                if (!seen.Add(source.Id))
                    Error(path, "Do not repeat a source ID in similarSources.");
                result.Add(CreateCitation(source, ""));
            }
            return result;
        }

        private SourceEvidence? ResolveSource(JsonElement value, string path)
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                Error(path, "Use a nonempty string containing an actual source ID.");
                return null;
            }
            var id = value.GetString()!;
            if (!evidence.TryGetValue(id, out var source) || source is null ||
                !string.Equals(source.Id, id, StringComparison.Ordinal))
            {
                Error(path, "The source ID does not exist in the captured evidence registry.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(source.Text) ||
                !IsSafePublicUrl(source.Url))
            {
                Error(path, "This source has unusable server evidence or an unsafe source URL; choose another source.");
                return null;
            }
            return source;
        }

        private string? ReadText(JsonElement value, string path, int maximumLength, bool allowLinks = false)
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                Error(path, "A nonempty string is required.");
                return null;
            }
            var text = value.GetString()!;
            if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength)
            {
                Error(path, $"Use a nonempty string of at most {maximumLength} characters.");
                return null;
            }
            if (!allowLinks && (text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                                text.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
                                ProseLink.IsMatch(text)))
            {
                Error(path, "Remove URLs and Markdown/autolinks; use source IDs in citation arrays instead.");
                return null;
            }
            return text.Trim();
        }

        private bool CheckObject(JsonElement value, string path, string[] required)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                Error(path, "A JSON object is required.");
                return false;
            }
            HashSet<string> seen = new(StringComparer.Ordinal);
            var valid = true;
            foreach (var property in value.EnumerateObject())
            {
                if (!required.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                {
                    Error(path, "Use only the required properties, exactly once each; unknown or duplicate properties are forbidden.");
                    valid = false;
                }
            }
            foreach (var name in required)
            {
                if (seen.Contains(name))
                    continue;
                Error($"{path}.{name}", "This property is required and cannot be omitted.");
                valid = false;
            }
            return valid;
        }

        private bool CheckArray(JsonElement value, string path, int maximumLength)
        {
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximumLength)
                return true;
            Error(path, $"An array of at most {maximumLength} items is required; use [] when empty.");
            return false;
        }

        private void Error(string path, string message)
        {
            // Paths are constructed exclusively from schema constants and numeric indexes.
            if (errors.Count < 64)
                errors.Add($"{path}: {message}");
        }
    }

    private static AnswerCitation CreateCitation(SourceEvidence source, string quote) =>
        new(source.Id, source.Url, string.IsNullOrWhiteSpace(source.Title) ? source.Url : source.Title,
            quote, source.Author, source.License, source.External);

    private static string NormalizeWhitespace(string value)
    {
        var result = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
                result.Append(' ');
            result.Append(character);
            pendingSpace = false;
        }
        return result.ToString();
    }

    private static string ExtractOriginalQuote(string source, int match, string normalizedQuote)
    {
        var normalizedIndex = 0;
        var start = -1;
        var pendingSpace = false;
        for (var index = 0; index < source.Length; index++)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                pendingSpace = normalizedIndex > 0;
                continue;
            }
            if (pendingSpace)
                normalizedIndex++;
            if (normalizedIndex == match)
                start = index;
            if (normalizedIndex == match + normalizedQuote.Length - 1)
            {
                var length = index - start + 1;
                // Very large source whitespace runs are represented by normalized whitespace, not clipped.
                return length <= 400 ? source.Substring(start, length) : normalizedQuote;
            }
            normalizedIndex++;
            pendingSpace = false;
        }
        throw new InvalidOperationException("A validated quotation could not be mapped to its captured source.");
    }

    private static bool IsSafePublicUrl(string value)
    {
        // Syntactic screening only; fetching, DNS checks and approval belong to the evidence producer.
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length > 0 || !uri.IsDefaultPort)
            return false;

        try
        {
            _ = new SourceAccessPolicy(uri);
            return true;
        }
        catch (SourceAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
