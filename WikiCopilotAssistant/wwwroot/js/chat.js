"use strict";

const statusMessage = document.getElementById("connection-message");
const dialog = document.getElementById("login-dialog");
const sourceForm = document.getElementById("source-form");
const startButton = document.getElementById("prepare-button");
const refreshButtons = document.querySelectorAll("[data-refresh]");
const stopButton = document.getElementById("stop-research");
const approvalCard = document.getElementById("host-approval");
const researchError = document.getElementById("research-error");
const continuationForm = document.getElementById("continuation-form");
const followupQuestion = document.getElementById("followup-question");
const sendFollowupButton = document.getElementById("send-followup");
const moreResearchButton = document.getElementById("more-research");
const terminalStates = new Set(["completed", "cancelled", "timed_out", "failed"]);
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
let previousAnswer = "";
let previousHistory = "";
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
    document.querySelector("#reset-form button").disabled = actionPending;
    const canContinue = continuationReady();
    followupQuestion.disabled = !canContinue;
    sendFollowupButton.disabled = !canContinue || !followupQuestion.value.trim();
    moreResearchButton.disabled = !canContinue;
    document.getElementById("followup-count").textContent =
        `${followupQuestion.value.length.toLocaleString("tr-TR")} / 8.000 karakter`;
}

function continuationReady() {
    return ready && !checking && researchLoaded && !actionPending &&
        currentResearch?.isActive === false && terminalStates.has(currentResearch?.state) &&
        currentResearch?.canContinue === true;
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

async function requestJson(url, method = "GET", fields, expectedStatus) {
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
                response.status === 409 ? "Sohbet başka bir sekmede değişmiş veya hâlâ işlem yapılıyor. Durum yenilendikten sonra tekrar deneyin; yazdığınız soru korunuyor." :
                    "İşlem tamamlanamadı. Uygulama bağlantısını kontrol edip yeniden deneyin."));
    }
    if (expectedStatus && response.status !== expectedStatus)
        throw new Error("İstek kabulü doğrulanamadı. Yazdığınız soru korunuyor; sohbet durumunu kontrol edip tekrar deneyin.");
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
        card.id = `source-${source.id}`;
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
            source.external ? "Ayrıca izin verdiğiniz dış kaynak" : "Başlangıç sitesindeki kaynak",
            source.truncated ? "Kaynak kısaltılmıştır" : null
        ].filter(Boolean).join(" · ");
        card.append(heading, address, text, attribution);
        container.append(card);
    }
    document.getElementById("no-sources").hidden = sources.length > 0;
}

function sourceLink(citation) {
    const link = document.createElement("a");
    let url;
    try { url = new URL(citation.url); } catch { url = null; }
    if (url && ["http:", "https:"].includes(url.protocol) && !url.username && !url.password) {
        link.href = url.href;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
    }
    link.textContent = `[${citation.sourceId}] ${citation.title || citation.url}`;
    return link;
}

function renderCitations(citations, container) {
    for (const citation of citations) {
        const evidence = document.createElement("details");
        evidence.className = "citation";
        const summary = document.createElement("summary");
        summary.textContent = `[${citation.sourceId}] Eşleşen alıntıyı ve kaynağı göster`;
        const quote = document.createElement("blockquote");
        quote.textContent = citation.quote;
        const metadata = document.createElement("p");
        metadata.className = "field-hint";
        metadata.textContent = [
            citation.external ? "Ayrıca izin verilen dış kaynak" : "Verilen sitenin kaynağı",
            citation.author, citation.license
        ].filter(Boolean).join(" · ");
        evidence.append(summary, quote, sourceLink(citation), metadata);
        container.append(evidence);
    }
}

function renderStatements(statements, element, steps = false) {
    element.replaceChildren();
    for (const statement of statements) {
        const item = document.createElement("div");
        item.className = "answer-statement";
        if (steps) {
            const label = document.createElement("p");
            label.className = "statement-label";
            label.textContent = statement.citations.length ? "Kaynak dayanağı olan öneri" : "Copilot önerisi · kaynakla doğrulanmış bilgi değildir";
            item.append(label);
        }
        const text = document.createElement("p");
        text.className = "answer-text";
        text.textContent = statement.text;
        item.append(text);
        renderCitations(statement.citations, item);
        element.append(item);
    }
}

function renderParagraphs(texts, element, emptyText) {
    element.replaceChildren();
    for (const text of texts.length ? texts : [emptyText]) {
        if (!text) continue;
        const paragraph = document.createElement("p");
        paragraph.className = "answer-text";
        paragraph.textContent = text;
        element.append(paragraph);
    }
}

function renderAnswer(snapshot) {
    const answer = snapshot?.answer;
    const signature = JSON.stringify([answer, snapshot?.validationState, snapshot?.validationIssues]);
    if (signature === previousAnswer) return;
    previousAnswer = signature;
    const container = document.getElementById("research-answer");
    container.hidden = !answer;
    container.replaceChildren();
    const issues = snapshot?.validationIssues || [];
    document.getElementById("answer-validation-errors").hidden = !issues.length;
    const issueList = document.getElementById("validation-issues");
    issueList.replaceChildren();
    for (const issue of issues) {
        const item = document.createElement("li");
        item.textContent = issue;
        issueList.append(item);
    }
    if (answer) renderStructuredAnswer(answer, snapshot.validationState, container);
}

function renderStructuredAnswer(answer, validationState, container) {
    const content = document.getElementById("structured-answer-template").content.cloneNode(true);
    const part = name => content.querySelector(`[data-answer="${name}"]`);
    part("validation-note").textContent = validationState === "no_evidence"
        ? "Bu araştırmada okunmuş kaynak bulunamadı. Aşağıdakiler yalnızca Copilot yorumlarıdır; kaynakla doğrulanmış bilgi değildir."
        : "Kaynak kimlikleri ve alıntılar okunan metinle eşleştirildi. Bu kontrol, iddia ve yorumların anlamsal doğruluğunu garanti etmez.";
    part("no-source-facts").hidden = answer.sourceFacts.length > 0;
    renderStatements(answer.sourceFacts, part("source-facts"));
    renderParagraphs(answer.commentary, part("model-commentary"), "Ek model yorumu bulunmuyor.");
    renderStatements(answer.suggestedSteps, part("suggested-steps"), true);
    if (!answer.suggestedSteps.length)
        renderParagraphs([], part("suggested-steps"), "Ek bir adım önerilmedi.");
    part("answer-uncertainties").hidden = !answer.uncertainties.length;
    renderParagraphs(answer.uncertainties, part("uncertainties"));
    part("similar-sources-section").hidden = !answer.similarSources.length;
    const similar = part("similar-sources");
    for (const citation of answer.similarSources) {
        const entry = document.createElement("p");
        entry.append(sourceLink(citation));
        if (citation.external) entry.append(document.createTextNode(" · İzin verilen dış kaynak"));
        similar.append(entry);
    }
    container.append(content);
}

function renderHistory(history) {
    const signature = JSON.stringify(history);
    if (signature === previousHistory) return;
    previousHistory = signature;
    const container = document.getElementById("conversation-turns");
    const openTurns = new Set(Array.from(container.children)
        .filter(turn => turn.open).map(turn => turn.dataset.turnId));
    const turns = history.map((entry, index) => {
        const turn = document.createElement("details");
        turn.className = "conversation-turn";
        turn.dataset.turnId = entry.id;
        turn.open = openTurns.has(String(entry.id));
        const summary = document.createElement("summary");
        summary.textContent = `${index + 1}. ${entry.moreResearch ? "Daha fazla araştırma · Önceki sorular ve açık kalan noktalar" : entry.question}`;
        const message = document.createElement("p");
        message.className = "field-hint";
        message.textContent = entry.message || "";
        const answer = document.createElement("div");
        if (entry.answer) {
            renderStructuredAnswer(entry.answer, entry.validationState, answer);
        } else {
            const notice = document.createElement("p");
            notice.className = "answer-warning";
            notice.textContent = "Bu tur için yayımlanmış bir yanıt bulunmuyor. Okunan kaynaklar aşağıda korunur.";
            answer.append(notice);
        }
        turn.append(summary, message, answer);
        return turn;
    });
    container.replaceChildren(...turns);
    document.getElementById("conversation-history").hidden = !history.length;
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
            validating: "Kaynaklar ve alıntılar kontrol ediliyor",
            completed: "Araştırma tamamlandı",
            cancelled: "Araştırma durduruldu",
            timed_out: "Araştırma zaman aşımına uğradı",
            failed: "Araştırma tamamlanamadı"
        };
        document.getElementById("research-title").textContent = headings[snapshot.state] || "Araştırma";
        document.getElementById("research-message").textContent = snapshot.message +
            (snapshot.isActive && terminalStates.has(snapshot.state)
                ? " Oturum temizliği sürüyor; devam etmek için bitmesini bekleyin." : "");
        document.getElementById("research-count").textContent = `${snapshot.toolCalls} araç çağrısı · Sabit çağrı sınırı yok`;
        document.getElementById("conversation-source").textContent = `Bu sohbetin başlangıç kaynağı: ${snapshot.sourceUrl || ""}`;
        document.getElementById("approved-hosts").textContent =
            `Bu sohbette izin verilen siteler: ${(snapshot.approvedHosts || []).join(", ")}`;
        document.getElementById("current-question-section").hidden = false;
        document.getElementById("current-question-title").textContent = snapshot.moreResearch ? "Daha fazla araştırma" : "Şu anki sorunuz";
        document.getElementById("current-question").textContent = snapshot.moreResearch
            ? "Önceki sorular ve açık kalan noktalar için ek araştırma." : snapshot.question || "";
        document.getElementById("current-more-research").hidden = !snapshot.moreResearch;
        document.getElementById("continuation-section").hidden = false;
        document.getElementById("continuation-message").textContent = snapshot.continuationMessage ||
            (snapshot.isActive ? "Araştırma ve oturum temizliği tamamlanınca devam edebilirsiniz." :
                snapshot.canContinue ? "Takip sorusu gönderebilir veya aynı soruyu daha fazla araştırabilirsiniz." :
                    "Bu sohbete şu anda devam edilemiyor. Yeni bir sohbet başlatabilirsiniz.");
        const characters = Number.isFinite(snapshot.contextCharacters) ? snapshot.contextCharacters : 0;
        document.getElementById("context-capacity").textContent = snapshot.isActive
            ? "Araştırma ve temizlik tamamlanınca sonraki devam bağlamının boyutu hesaplanacak. Sınır 128.000 karakterdir; aşılırsa geçmiş kısaltılmaz, yeni sohbet gerekir."
            : `Sonraki devam isteğinin bağlamı: ${characters.toLocaleString("tr-TR")} / 128.000 karakter (güncel sonuç dahil sohbet geçmişi ve kaynak önizlemeleri). Sınır aşılırsa geçmiş kısaltılmaz; yeni sohbet gerekir.`;
        const approval = snapshot.approval;
        approvalCard.hidden = !approval;
        if (approval) {
            document.getElementById("approval-host").textContent = approval.host;
            document.getElementById("approval-message").textContent = approval.message;
        }
        renderAnswer(snapshot);
        renderHistory(snapshot.history || []);
        renderSources(snapshot.sources || []);
    } else {
        approvalCard.hidden = true;
        document.getElementById("research-title").textContent = "Araştırma";
        document.getElementById("research-message").textContent = "";
        document.getElementById("research-count").textContent = "";
        document.getElementById("approved-hosts").textContent = "";
        document.getElementById("conversation-source").textContent = "";
        document.getElementById("current-question-section").hidden = true;
        document.getElementById("current-question").textContent = "";
        document.getElementById("current-more-research").hidden = true;
        document.getElementById("continuation-section").hidden = true;
        document.getElementById("continuation-message").textContent = "";
        document.getElementById("context-capacity").textContent = "";
        renderAnswer(null);
        renderHistory([]);
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

async function researchAction(url, fields, options = {}) {
    if (actionPending) return false;
    actionPending = true;
    const expectedGeneration = ++generation;
    clearTimeout(researchTimer);
    showResearchError("");
    pollingFailed = false;
    updateControls();
    try {
        if (options.confirmReplacement) {
            const latest = await requestJson("/research/status");
            if (expectedGeneration !== generation) return false;
            renderResearch(latest);
            if (latest.isActive) {
                throw new Error("Bu sohbet hâlâ işlem yapıyor. Yeni sohbet başlatmadan önce bitmesini bekleyin veya durdurun.");
            }
            if (currentResearch && !confirm(
                `Mevcut sohbet (${currentResearch.sourceUrl || "başlangıç kaynağı"}) silinsin mi? Aynı URL olsa bile tüm geçmiş, kaynaklar ve site izinleri temizlenerek yeni sohbet başlatılacak.\nYeni kaynak: ${fields.get("SourceUrl")}`)) return false;
        }
        const snapshot = await requestJson(url, "POST", fields, options.expectedStatus);
        if (expectedGeneration !== generation) return false;
        renderResearch(snapshot);
        return true;
    } catch (error) {
        if (expectedGeneration !== generation) return false;
        document.getElementById("research-panel").hidden = false;
        showResearchError(error.message);
        return false;
    } finally {
        if (expectedGeneration === generation) {
            actionPending = false;
            updateControls();
            clearTimeout(researchTimer);
            researchTimer = setTimeout(pollResearch, 500);
        }
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
    const fields = new FormData(sourceForm);
    fields.set("SourceUrl", newSource);
    fields.set("Question", document.getElementById("Question").value.trim());
    document.getElementById("form-feedback").textContent = "";
    if (await researchAction("/research/start", fields, { confirmReplacement: true, expectedStatus: 202 })) {
        followupQuestion.value = "";
        updateControls();
        document.getElementById("research-panel").scrollIntoView({ behavior: "smooth", block: "start" });
    }
});
followupQuestion.addEventListener("input", updateControls);
continuationForm.addEventListener("submit", async event => {
    event.preventDefault();
    if (!continuationReady() || !continuationForm.reportValidity()) return;
    const question = followupQuestion.value.trim();
    if (!question) return;
    if (await researchAction("/research/continue",
        { id: currentResearch.id, question, moreResearch: "false" }, { expectedStatus: 202 })) {
        followupQuestion.value = "";
        updateControls();
        document.getElementById("current-question-section").scrollIntoView({ behavior: "smooth", block: "start" });
    }
});
moreResearchButton.addEventListener("click", async () => {
    if (!continuationReady()) return;
    if (await researchAction("/research/continue",
        { id: currentResearch.id, moreResearch: "true" }, { expectedStatus: 202 })) {
        document.getElementById("current-question-section").scrollIntoView({ behavior: "smooth", block: "start" });
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
    if (actionPending || !confirm("Bu sohbet durdurulup tüm geçmiş, kaynaklar ve site izinleri silinsin mi?")) {
        event.preventDefault();
        return;
    }
    generation++;
    actionPending = true;
    clearTimeout(researchTimer);
    updateControls();
});
window.addEventListener("pageshow", () => {
    generation++;
    actionPending = false;
    researchLoaded = false;
    updateControls();
    checkConnection(false);
    pollResearch();
});
