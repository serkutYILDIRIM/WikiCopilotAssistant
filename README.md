# WikiCopilotAssistant

A local ASP.NET Core MVC assistant being developed to research a supplied
documentation/site URL using GitHub Copilot CLI and answer with clearly
separated sources, model commentary, and suggested next steps.

## Current status

The web research step connects the MVC source/question form to actual Copilot
research. It includes progress polling, exact-host approval, Stop, and source
cards populated only from successfully read documents.

Answers are separated into source-backed statements, Copilot commentary, and
suggested steps. Source-backed statements require IDs assigned by the server
and short quotations matching the text actually read. URLs, titles, attribution,
and external-source labels come from the server's evidence registry, not from
model-generated links.

**Reference/quotation checks are not a guarantee of semantic truth.** A matching
quote does not prove that the model interpreted it correctly. Follow-up questions
reuse the conversation's questions, checked answers, and captured source context
from RAM. Each turn uses a fresh Copilot session, which is cleaned up afterwards.

The `--check-copilot` command checks the runtime version, authentication
availability, and available model count. It does not send a model prompt,
open source websites, or print credentials/account names. A successful
connection check does not establish that web research tools work.

## Requirements

- A stable .NET 10 SDK.
- GitHub Copilot CLI and an account with the required Copilot access.
- Internet access for Copilot services.
- An installed Microsoft Edge for optional JavaScript page rendering.

No database, Docker service, or separate frontend installation is required.
No unit tests or test projects are included.

## Downloads require an explicit setup action

The SDK package is pinned to `GitHub.Copilot.SDK` 1.0.13. Its matching
runtime is 1.0.83. The approved shared reader dependencies are
`AngleSharp` 1.8.1 and `Microsoft.Playwright` 1.62.0.
Automatic Copilot runtime downloading is disabled in the project.
Browser installation is a separate decision: the application uses existing
Edge and must not download Chromium automatically.

The following setup commands download NuGet packages and the Copilot runtime.
Run them only when you explicitly want to allow those downloads:

```powershell
dotnet restore WikiCopilotAssistant.sln
dotnet build WikiCopilotAssistant\WikiCopilotAssistant.csproj --no-restore -p:CopilotSkipCliDownload=false
```

## Sign in from your own terminal

Each user signs in directly through Copilot CLI on their own computer:

1. Open a terminal or PowerShell window.
2. If Copilot CLI is already installed, run:

   ```powershell
   copilot login
   ```

3. Follow the CLI's browser instructions and sign in with your own GitHub account.
4. Return to the application and check the connection again.

Do not paste passwords, tokens, or verification codes into this application,
project files, issue reports, or chat messages. The application does not
perform login on your behalf.

If the `copilot` command is missing, consult the
[official CLI installation instructions](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/install-copilot-cli)
and explicitly approve/install it yourself. The application does not install
or update the CLI automatically.

The web interface includes a dismissible **Copilot'a nasıl bağlanırım?**
information popup with these steps and a **Bağlantıyı yeniden kontrol et**
button. No password, token or verification-code input is present.
The page opens the help when authentication is reported missing; help also
remains available from the header. Checking again queries the runtime rather
than assuming login succeeded, recreating an unavailable runtime when necessary.

## Build without downloading

If the Windows x64 runtime has already been downloaded into this project's
SDK cache, reuse it through the SDK's supported local-binary option:

```powershell
$runtime = (Resolve-Path "WikiCopilotAssistant\obj\Debug\net10.0\copilot-cli\1.0.83\win32-x64\prebuilds\win32-x64\copilot-runtime.exe").Path
dotnet build WikiCopilotAssistant\WikiCopilotAssistant.csproj --no-restore -p:CopilotSkipCliDownload=true "-p:CopilotCliBinaryPath=$runtime"
```

This example is specific to the currently verified Windows x64 cache layout.
It copies the existing runtime into the build output without downloading it.
If the cache or restored packages are absent, stop and complete the explicit
setup action instead. A fresh clone does not contain these ignored files.

## Check the connection

Run the already-built application without triggering restore or another build:

```powershell
dotnet WikiCopilotAssistant\bin\Debug\net10.0\WikiCopilotAssistant.dll --check-copilot
```

Exit code `0` means connectivity and model availability were confirmed.
Exit code `1` means a reported prerequisite is missing, the CLI is incompatible,
or the check timed out. Authentication reported by this check applies to the
selected runtime, not to every Copilot application installed on the computer.

### Selecting an existing CLI

The connection check also accepts an existing CLI through a local environment
variable. Its value is not written to project files or printed by the check:

```powershell
$env:WIKICOPILOT_CLI_PATH = (Get-Command copilot).Source
dotnet WikiCopilotAssistant\bin\Debug\net10.0\WikiCopilotAssistant.dll --check-copilot
Remove-Item Env:WIKICOPILOT_CLI_PATH
```

Explicitly selected CLIs run with automatic updates disabled. CLI 1.0.18
was found incompatible with SDK 1.0.13 during the initial protocol handshake;
this is not evidence that the user is signed out.

For offline diagnosis only, the already-downloaded full CLI 1.0.83 can be
selected instead when a compatible Node.js is already installed:

```powershell
$env:WIKICOPILOT_CLI_PATH = (Resolve-Path "WikiCopilotAssistant\obj\Debug\net10.0\copilot-cli\1.0.83\win32-x64\index.js").Path
dotnet WikiCopilotAssistant\bin\Debug\net10.0\WikiCopilotAssistant.dll --check-copilot
Remove-Item Env:WIKICOPILOT_CLI_PATH
```

Do not install Node.js just to run this optional diagnosis without explicitly
choosing to do so. If a compatible CLI still cannot see an existing login,
repeat the check in the terminal where you signed in. Any required login is
performed by the user through that CLI, never by supplying credentials to
this application. Do not share raw authentication output or credential files.

## Open the local web interface

After the approved dependency setup and a successful build:

```powershell
dotnet run --project WikiCopilotAssistant --no-build --no-restore --launch-profile http
```

Open `http://localhost:5242`. This command uses the existing build and does not
restore packages or download a browser/runtime.

- Wait for the Copilot connection status, or use the terminal-login help.
- Enter a public source URL and question, then choose **Araştırmayı başlat**.
  This sends a real prompt to Copilot and consumes your account's allowance.
- Watch the progress and successfully read source cards. If another hostname
  is needed, explicitly allow or deny it before the page is opened.
- Use **Durdur** to cancel. Wait for cleanup before starting another operation.
  Closing or reloading the page is not a Stop command.
- Use **Yeni sohbet** to cancel and clear the current operation. Changing the
  source or starting another initial question prompts before replacing the old
  conversation in the browser. The replacement inherits no history or permissions.
- A completed answer shows source statements with expandable matching quotations,
  a separately labeled Copilot interpretation, and suggestions labeled by their
  source support. Search snippets alone cannot support a source statement.
- Use the follow-up field below the result to describe what you tried, supply a
  version/error, or ask another question. Earlier turns remain available in the
  conversation history, including their citations and any failed/cancelled status.
- **Daha fazla araştır** starts a new turn about the unresolved questions, using
  the same context. It is also available after a failure, Stop, or timeout once
  cleanup finishes. Nothing restarts automatically.

Only loopback HTTP(S) listening addresses are accepted. Foreign Host/Origin
requests and cross-site browser requests are rejected. State-changing requests
require an antiforgery token; session/antiforgery cookies are HttpOnly and
SameSite Strict. Draft state and data-protection keys are in memory, not a
database or project file. Drafts expire after 30 minutes without session
activity or when the app restarts. Shared browser tabs share a session and
its active research; separate sessions cannot control each other's operations.
No fixed research-call cap is applied; finite research, read and approval
timeouts still apply.

### Research controls and limits

- One active operation per browser session, including cleanup. A second start
  returns HTTP 409 instead of launching another agent.
- Another hostname or subdomain pauses `read_source` until the corresponding
  on-screen decision is submitted. Approval and rejection are scoped to the
  conversation and survive follow-up turns. A new initial start never inherits
  them. Old decision IDs cannot be replayed, and prompts cannot approve a host.
- Stop immediately freezes publication, cancels pending decisions, requests
  SDK abort, and drains running readers before releasing the runtime lease.
  Previously captured source cards remain available. A stale operation ID
  cannot stop or approve a newer operation.
- The runtime connection is leased during research. Connection refresh cannot
  replace an actively used client. App shutdown cleans research before the
  connection service.
- Research is kept in RAM. Completed snapshots expire after the configured
  retention interval; drafts with no research expire after inactivity. Reset
  waits for cleanup before clearing state. Restart removes all in-app state.
- Safety/display limits remain distinct from a research-call cap: source cards
  retain up to 128 document versions with 500-character previews; full captured
  evidence is bounded to 4 Mi characters per conversation. A conversation records
  up to 128 host decisions. Structured model JSON and individual answer sections
  also have finite size limits. Reaching a source-memory limit is disclosed and
  the omitted document cannot be cited; no research-call count limit is imposed.

### Follow-up context and its limit

The application does not leave a Copilot session running between questions.
It supplies each new turn with the previous questions, their checked answers
(or failure status), and a catalog of previously captured source IDs with short,
explicitly marked previews. Full captured evidence stays in RAM for quotation
validation. The agent can reread a source when it needs more than the previous
quotations/previews. Changed source text receives a new ID; old quotations retain
their original evidence version. Prior model commentary remains commentary,
not a new source of facts.

The serialized history/catalog is limited to **128,000 characters**. The UI shows
its size after cleanup. When it exceeds the limit, follow-up and further research
are refused with an explicit request to start a new conversation. Existing
answers are still visible; history is not silently discarded or summarized.
This is a context/memory limit, not a limit on research tool calls or pages.
Replaying context sends it to Copilot again and can increase account usage.

Only the latest turn in the same browser session can be continued. Duplicate
submissions, stale IDs, and other sessions cannot append to it. A failed follow-up
does not erase earlier answers. Reloading restores the in-memory conversation;
reset, replacement, expiry, and application restart remove it. There is no
database, local transcript export, or persistent in-app chat storage.

Optional configuration uses the existing .NET configuration system:

| Environment variable | Default | Accepted range |
|---|---:|---:|
| `Research__TimeoutSeconds` | 300 | 1–3600 |
| `Research__ApprovalTimeoutSeconds` | 60 | 1–300 |
| `Research__RetentionMinutes` | 30 | 1–120 |
| `Research__CleanupTimeoutSeconds` | 15 | 1–60 |

Do not place credentials in these settings. Defaults require no extra
configuration. Read transport/browser safety deadlines may be shorter than
the overall research deadline.

The polling API is session-owned: `GET /research/status`; antiforgery-protected
`POST /research/start`, `/research/continue`, `/research/stop`,
`/research/approve` and `/chat/reset`.
The browser presents safe failures rather than raw SDK exceptions or account data.

### How answer checking works

1. `read_source` records the successfully read source body in RAM and returns
   its immutable source ID to Copilot. Reading the same URL with changed content
   produces a new evidence version; earlier quotes are not checked against an
   overwritten document. Failed reads and search snippets receive no evidence ID.
2. Copilot returns a structured JSON answer. Every source-backed statement
   requires at least one short quotation with an existing source ID. Suggestions
   without citations are explicitly model suggestions.
3. The validator checks the shape, IDs, safe source links, and quotation matches
   against captured full text (not just the 500-character UI preview). Whitespace
   differences may be normalized; words and casing may not be invented.
4. If the response fails, at most one correction is requested. New source tools
   are disabled during that correction and the original research deadline still
   applies. An answer that remains invalid is not published. Actual source cards
   remain available, including when the model fails.
5. With no read evidence, only explicitly labeled model commentary/suggestions
   can be displayed. The UI states that no source-backed facts are available.

Only the server creates clickable citations. Model-authored URLs are not accepted
in answer prose; citations and related-page references must point to registry IDs.
Approved external sites are marked separately from the starting site's sources.
Source access approval, stop/reset isolation and safe text rendering still apply
during answer validation.

The manual validator command fetches one public source and reads answer JSON
from standard input. It does not access local files or call a model:

```powershell
$json | dotnet WikiCopilotAssistant\bin\Debug\net10.0\WikiCopilotAssistant.dll --validate-answer https://example.com
```

`$json` must use the same answer contract as the research tool: `sourceFacts`,
`commentary`, `suggestedSteps`, `uncertainties`, and `similarSources` arrays.
IDs start at `S1` for the documents returned by that source read. Errors report
validation categories, never the raw JSON content. This is a manual application
diagnostic, not a unit-test project.

The background Copilot client checks startup readiness without asking a model
question. Expected connection failures are shown as safe messages, without
account identifiers or raw CLI errors. Terminal login remains entirely yours.

## Manual live web check

After building, the following optional diagnostic sends generic React questions
to Copilot, using native web search and the shared `read_source` tool on public
React and Stack Overflow pages. Copilot chooses the URLs; the application does
not run an independent site-crawling loop. It uses Copilot allowance and internet access,
but does not install or update packages:

```powershell
dotnet WikiCopilotAssistant\bin\Debug\net10.0\WikiCopilotAssistant.dll --check-copilot-web
```

Each source gets a separate session and a three-minute research timeout,
with no fixed tool-call count limit. Only HTTPS pages on that source's exact host
are allowed; other tools and source hosts are rejected. This is a manual live
capability check, not a unit test or the final chat interface.

Tool results remain in application memory. Output shows tool outcome metadata
and a model response explicitly marked as not yet citation-validated. Exit
code `0` requires successful searches and successful page reads for both sites;
a successful search does not conceal failed page reads.

Earlier native-only checks showed successful React reading but access-denied
responses from Stack Overflow page reads. The implemented shared reader uses
HTTP/HTML, the official API for supported Stack Overflow URLs, and isolated
browser rendering for public JavaScript content. Live Copilot runs successfully
searched and read both React documentation and actual Stack Overflow question
and answer bodies through this reader. Search snippets are not substituted for
source bodies.

Source text is supplied inline to Copilot; SDK large-output file offloading is
disabled so the agent does not need local filesystem tools. Large pages return
explicitly marked excerpts; an actual section-anchor URL can select a later
section. Stack Overflow answer pagination is exposed through continuation URLs.
This older diagnostic is only a connectivity/reader check: it does not run the
final citation validator or the web interface's cancellation and cross-site
approval flows. The MVC research flow implements those controls and the answer
checking described above.

The planned application likewise has no fixed research-call count limit.
User-approved site scope, a Stop action, finite technical timeouts, and
respect for access restrictions remain mandatory. Unlimited call count does
not mean guaranteed access to every site. The reading strategy must handle
source types consistently rather than treating every access failure as a
Stack Overflow-specific problem.

## Read one source without a model prompt

The manual source-reading command uses the same shared reader as the Copilot
tool, but does not send a question to the model. It prints outcome metadata,
not the full source text:

```powershell
dotnet WikiCopilotAssistant\bin\Debug\net10.0\WikiCopilotAssistant.dll --read-source https://react.dev/learn
dotnet WikiCopilotAssistant\bin\Debug\net10.0\WikiCopilotAssistant.dll --read-source https://react.dev/learn --render-source
```

`--render-source` requests browser rendering. `--source-scope <url>` optionally
sets a different initial source for checking hostname approval behavior.
Other hosts are not automatically approved by this command. Ctrl+C cancels
reading; a finite technical timeout also applies. For manual cancellation
checks, `--read-timeout-ms <1..180000>` can shorten that deadline.

## Verified technical scenarios

These were manual checks against the application, not unit tests. The following
table records the earlier integration milestones; references to drafts describe
the behavior before structured answer checking was added.

| Scenario | Observed outcome |
|---|---|
| Copilot CLI/runtime 1.0.83 with SDK 1.0.13 | Existing terminal login recognized; model access available. |
| React and Microsoft Learn | Real HTML source text and links extracted. |
| Long React reference page | Truncation disclosed; a later section-anchor URL returned the requested section. |
| Stack Overflow | Question and answer bodies read through the public official API; author/license attribution and second answer page available. |
| Copilot research | Native search plus `read_source` produced responses from both target sites without filesystem access. Responses are not yet citation-validated. |
| Existing Edge | React and a JavaScript-generated public page rendered in verified temporary profiles and closed successfully. |
| Source access controls | File/loopback URLs, private DNS destinations, and redirects to private or unapproved hosts rejected. |
| Cancellation | Short operation deadlines stopped ordinary and browser source reads without publishing success. |
| MVC draft form | Valid input prepared a RAM-only draft; empty questions and invalid/private source URLs returned validation errors. |
| Login help | Popup opened/closed, including Escape with focus return; no credential fields; real connection refresh succeeded. |
| Missing CLI | A separate instance with an invalid CLI path reported unavailable and rejected draft submission; the real login was not modified. |
| Web request controls | Missing antiforgery token, foreign Origin and foreign Host were rejected. |
| Draft isolation | Independent sessions did not share drafts; New conversation and application restart cleared the draft. |
| Safe rendering | Script-like question text stayed text, with no injected script element. The 390-pixel mobile layout had no horizontal overflow. |
| Live web research | A real form request started native search and source reading, survived page reload, and produced an explicitly unverified draft with actual React source cards. |
| Stack Overflow web form | The final web build completed native search plus API reading and displayed 11 real question/answer source cards with author/license metadata and a Turkish draft. |
| Single active operation | Concurrent start returned 409; connection refresh during the active lease remained safe. |
| External host approval | A requested external page was not in source cards before approval; approving example.com enabled an actual read. A separate MDN subdomain required its own decision. |
| Approval rejection/expiry | Rejected subdomain content produced no source card. A real two-second approval deadline rejected a delayed allow request with 409 and did not grant the host. |
| Stop and stale events | Browser Stop and stop during a pending approval prevented answer publication. Replayed approvals/stops returned 409; reset and replacement IDs stayed isolated. |
| Owner isolation | A different session saw idle and could neither stop nor approve the browser's operation. |
| Research timeout | A separate two-second deadline instance became timed_out, finished cleanup, and published no answer; no production defaults were changed. |
| Research result rendering | The final 390-pixel layout had no horizontal overflow; model text created no script elements or clickable model-generated links. |

These results establish the initial integration, not universal website support
or a complete security certification. Login/paywall/CAPTCHA content is not
supported. Browser rendering blocks frames, workers, WebSockets, downloads,
media and known analytics/font hosts; pages requiring those features may not
be fully readable. Other script hosts require approval rather than being
silently loaded. The CLI diagnostic reports that need; the MVC interface pauses
for an explicit hostname approval.

### Structured answer acceptance

The structured-answer build was also exercised with the actual Copilot runtime,
public source reads, browser form submissions, and session-owned HTTP requests:

| Scenario | Observed outcome |
|---|---|
| React useEffect research | Completed with four source-backed statements, five matching citations, two separately labeled commentary items, and two suggestions. One correction was needed. |
| Stack Overflow dependency-array question | Completed with three source-backed statements and five matching citations across a question and two answer permalinks. Citations retained author metadata and CC BY-SA 4.0 attribution; commentary and one suggestion were separate. One correction was needed. |
| Full evidence versus card previews | Three accepted React quotations came from beyond the 500-character source-card previews. Validation used the captured body, not the preview. |
| Source unavailable | A real HTTP 404 yielded no source cards or source facts; only labeled model commentary and explicit missing-evidence warnings were published after one correction. |
| External citation | An example.com read waited for approval from an example.org operation. Its accepted citation, source card, and related-page link were labeled as external; the starting source was not. |
| Invalid model answer | A Stack Overflow run still contained two nonmatching quotations after its single correction. No answer was published, validation errors were reported, and all 11 successfully read question/answer cards remained available. |
| Stop during correction | Stopping a real operation in the correction phase produced `cancelled`, completed cleanup, preserved its source card, and published no late answer. |
| Strict contract diagnostic | Actual-source stdin checks accepted exact and whitespace-normalized quotations. Unknown IDs, fabricated quotations, model URL fields, prose URLs, duplicate properties, null arrays, missing citations, malformed JSON, and an empty answer were rejected. Input exceeding 64,000 characters was rejected before fetching a source. |
| Browser presentation | Quotations expanded with the keyboard. A 390-pixel viewport had no horizontal overflow. Model-authored HTML-like text remained literal text instead of creating an element. |

Correction is bounded, not a promise that every model response will pass.
If it fails, inspect the retained source cards or start a new, more focused
question. A matching quotation still does not establish that the interpretation
or recommendation is correct.

Immutable URL/content-version keys and the disclosed evidence-memory limits were
inspected in code. A changing remote document at the same URL and exhaustion of
the entire evidence-memory budget were not artificially staged. These results
are specific to the structured-answer milestone.

### Follow-up acceptance

These are manual application checks, not unit tests:

| Scenario | Observed outcome |
|---|---|
| Actual React follow-up | The next checked answer recalled the user's React 19 version, attempted `setCount` change, and a generic conversation marker without those details being repeated in the follow-up. Earlier source IDs and the checked answer remained available. |
| Further research and Stop | A third turn was marked as further research. Stopping it suppressed its answer while retaining both earlier checked answers; another continuation remained possible after cleanup. |
| Context threshold | Exactly 128,000 serialized characters allowed continuation. A subsequent cancelled turn raised the context to 128,222 characters; the next POST returned 422 with an explicit limit notice and did not change the history or turn ID. Reset returned idle. |
| Ownership and stale requests | Another session, an old turn ID, duplicate submission, and continuation during an active turn returned 409. Empty/8,001-character questions returned 422; a request without the antiforgery token returned 400. |
| Source replacement | A source URL injected into a follow-up did not change its source. An explicit new initial start cleared history and retained only the new starting hostname. Reset invalidated the old continuation ID. |
| Conversation-scoped host approval | A Microsoft Learn host approved during React research remained approved in the follow-up, which read it without another approval prompt. Previously captured source IDs and the external-source label were retained even though the first answer had failed. A Wikipedia host had been explicitly denied. |
| Timeout continuation | In a separate two-second-deadline instance, a timed-out turn could be continued. Further research got a new ID and its own deadline, preserved the earlier timeout in history, and published no late answer. Production timeout settings were unchanged. |

## Browser privacy boundary

The approved design uses application-level isolation, not a separate operating
system account. Browser reading must launch a fresh temporary browser/context,
never attach to your existing Edge window or reuse your browser profile.
History, saved passwords, cookies and login state are not imported. Before
source navigation, the application verifies that its newly launched browser
uses a Playwright-created temporary profile. It does not read profile contents.
Local-file schemes and Copilot filesystem/shell tools are not available.

Source network requests use anonymous HTTP without browser cookies or
authorization headers. Public IP validation and pinned connections are shared
by ordinary HTTP reads and intercepted browser requests; redirects are checked
before following. A fail-closed browser proxy prevents unhandled requests from
falling through to ordinary browser networking.

Only the selected public source and explicitly approved hosts may be read.
Missing permission or access restrictions must be reported, not bypassed.
The application must not ask for a website password or load a personal browser
profile to get around a blocked page.

These are application controls, not a claim that a process running under your
Windows account has operating-system-level isolation from all of your files.

## Privacy

- Credentials are managed by Copilot outside the application source.
- The application must not store API keys, passwords, tokens, or personal
  filesystem paths in committed files.
- GitHub account identifiers, Copilot subscription/billing information,
  session files, and raw authentication responses must not be committed or
  included in application logs or shared diagnostic reports.
- Local environment files, credential containers, build output, and
  repository-local Copilot state are ignored by Git. Ignoring a file does
  not remove it if it was already committed.
- Copilot is an online service, not an offline model. Its own credential
  storage, session files, and synchronization policies are separate from
  the application's in-memory chat history.
