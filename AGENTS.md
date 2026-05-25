# QMA STRICT PORT PROTOCOL
## Desktop → Docker + Web UI (*arr-stack pattern)

---

## Architecture Overview

This project follows the *arr-stack pattern (Radarr, Sonarr, etc.):

```
┌───────────────────────────────────────────────────────┐
│                   DOCKER CONTAINER                    │
│                                                       │
│  ┌───────────────────┐  ║  ┌───────────────────────┐  │
│  │     BACKEND       │  ║  │       FRONTEND        │  │
│  │   (preserved)     │  ║  │    (new web UI)        │  │
│  │                   │  ║  │                       │  │
│  │  Business logic   │  ║  │  No backend imports   │  │
│  │  Data access      │  ║  │  No shared code       │  │
│  │  File handling    │  ║  │  No direct DB access  │  │
│  │  Scheduling       │  ║  │                       │  │
│  │                   │  ║  │  Talks to backend     │  │
│  │  ┌─────────────┐  │  ║  │  via REST API ONLY    │  │
│  │  │  REST API   │◄─┼──╬──┤                       │  │
│  │  │   layer     │  │  ║  │  Mirrors desktop UI   │  │
│  │  └─────────────┘  │  ║  │  functionality        │  │
│  └───────────────────┘  ║  └───────────────────────┘  │
│                         ║                             │
│              HARD BOUNDARY — never crossed            │
└───────────────────────────────────────────────────────┘
```

**The double line (║) is a hard boundary. Frontend and backend are independent. The REST API is the only crossing point.**

**Two separate work modes apply. Identify which mode you are in before every task.**

---

## ⚠️ Two Modes — Identify Before Every Task

### MODE A: Backend Work
You are porting existing desktop backend logic.
**Rule: strict port. Preserve everything. Add API layer only.**

### MODE B: Frontend Work
You are building a new web UI.
**Rule: functional mirror. Match desktop UI behavior. Do not copy desktop UI code.**

Print at the start of every task:
```
MODE: [A — Backend Port | B — Frontend Build]
REASON: [one line]
```

---

## ⛔ Separation Contract — Always Active (Both Modes)

The frontend and backend are **independent deployable units**. They do not share code, imports, or internal knowledge of each other. This rule is always active regardless of mode.

### Hard Boundary Rules

**Frontend must NEVER:**
- Import or reference backend source files directly
- Access the database directly
- Call backend internal functions directly
- Share models, classes, or utility code with the backend
- Know anything about backend internals (file paths, DB schema, internal variable names)
- Be broken by a backend refactor that preserves the API response shape

**Backend must NEVER:**
- Import or reference frontend source files
- Embed frontend logic or rendering
- Return data shaped for a specific frontend component (shape data generically)
- Break the API contract when internal logic changes

**The API layer is the only crossing point:**
- Frontend sends HTTP requests to API endpoints
- Backend responds with JSON
- That is the entire relationship

### API Contract Rules

Once an API endpoint is defined, treat it as a contract:

- **Route** — do not change without user confirmation
- **Response shape** — do not change field names or structure without user confirmation
- **Status codes** — do not change without user confirmation

> If backend internal logic changes, the API response must remain identical. Backend changes must not require frontend changes. If they do, the boundary has been violated.

### Separation Checklist (Both Modes)

Before submitting any file, confirm:

- [ ] Frontend file has zero imports from backend source
- [ ] Backend file has zero imports from frontend source
- [ ] All frontend↔backend communication goes through named API endpoints
- [ ] No shared utility files span both sides
- [ ] A backend change would not require a frontend code change

### Violation Detection

If about to create a shared module, shared type, or shared utility used by both sides, stop and write:

> `BOUNDARY VIOLATION: I was about to share [X] across frontend/backend. Options: [duplicate in each | move to backend and expose via API | ask user]`

---

## MODE A — Backend Port Rules

### What "Backend" Means Here
All existing desktop logic: business logic, data processing, file handling, scheduling, database access, configuration, service integrations. Everything the desktop app did internally.

### Goal
Preserve backend logic exactly. The only permitted additions are:
1. An HTTP/REST API layer to expose existing functions to the web frontend
2. Environment variable wiring for Docker
3. Docker infrastructure files

### MANDATORY PRE-FLIGHT (Mode A)

Print before any backend code output:

```
═══════════════════════════════════════════════════
MODE: A — Backend Port
SOURCE FILE: [file path : line range]
SOURCE EXCERPT:
[paste 5–15 lines of source being ported]

CHANGES:
- [PT] [specific change] → [equivalent] (reason: [Docker/API requirement])
- [ED] BLOCKED — [thing I was about to change but won't]

API ADDITIONS: [list new endpoints added, or "none"]
NEW ADDITIONS: none  — OR —  BLOCKED: see confirmation request
═══════════════════════════════════════════════════
```

### Mode A — Allowed Changes

| Change | Label | Notes |
|---|---|---|
| Add HTTP server / router | `[PT]` | Required to expose backend to web UI |
| Add REST API endpoints wrapping existing functions | `[PT]` | Endpoints call existing logic, do not replace it |
| Move hardcoded config to `.env` / environment variables | `[PT]` | Keep variable names from source |
| Replace absolute desktop file paths with Docker volume paths | `[PT]` | Document path mapping |
| Replace IPC / inter-process calls with internal function calls or REST | `[PT]` | |
| Replace desktop DB path with Docker volume or service | `[PT]` | Do not change schema |
| `Dockerfile`, `docker-compose.yml` | `[PT]` | Pre-approved, still document |

### Mode A — Blocked Changes

- Renaming variables, functions, or classes
- Restructuring logic or control flow
- Adding error handling not in source
- Adding logging not in source
- Changing database schema
- Replacing business logic with a "better" version
- Adding new background services not in source
- Any change not listed in Allowed Changes above

### Mode A — API Layer Rules

When adding REST API endpoints to expose backend logic:

- Each endpoint must map directly to an **existing** backend function
- Endpoint behavior must exactly match what the desktop function did
- Do not add endpoints for things that did not exist in the desktop app
- Do not add authentication middleware unless it existed in the desktop source
- Document every new endpoint in the diff summary

---

## MODE B — Frontend Build Rules

### What "Frontend" Means Here
A new web UI. There is no desktop UI code to port. The desktop UI (native windows, menus, dialogs) does not translate to web. You are building new code that **mirrors the desktop UI's functionality**.

### Goal
Build a web UI that gives the user the same capabilities they had in the desktop UI. Match behavior, not code.

### Source of Truth for Mode B
The desktop UI is your **functional specification**, not your code source. For every screen or feature you build, ask:
- What could the user do here in the desktop app?
- What did the user see here in the desktop app?
- Replicate that capability via web equivalents.

### MANDATORY PRE-FLIGHT (Mode B)

Print before any frontend code output:

```
═══════════════════════════════════════════════════
MODE: B — Frontend Build
DESKTOP EQUIVALENT: [what desktop screen/feature this mirrors]
FUNCTIONALITY BEING MIRRORED:
- [user action 1 from desktop]
- [user action 2 from desktop]
- [...]

API ENDPOINTS USED: [list backend endpoints this will call]
NEW UI COMPONENTS: [list — these are expected and permitted]
═══════════════════════════════════════════════════
```

### Mode B — Allowed

- New web UI components that mirror desktop UI panels/screens
- Web equivalents of desktop UI patterns (see table below)
- REST API calls to backend endpoints
- Web routing that mirrors desktop navigation structure
- Web-native equivalents of desktop interactions

### Mode B — Desktop UI → Web UI Equivalents

| Desktop UI Pattern | Web Equivalent |
|---|---|
| Native window / panel | Page route or modal |
| Native menu bar | Top nav / sidebar nav |
| Native context menu | Web context menu or dropdown |
| Native file picker | `<input type="file">` or path input field |
| Native alert / confirm dialog | Web modal / dialog component |
| Native notifications | Toast / snackbar or browser Notification API |
| Settings window | Settings page or slide-over panel |
| System tray | Status indicator in nav bar |
| Native table / list view | Web data table |
| Native form | Web form (no `<form>` tag if using React — use controlled inputs + button) |
| Status bar | Footer status bar or inline status indicator |

### Mode B — Blocked

- Copying desktop UI source code directly into frontend (it won't work and isn't a port)
- Adding features the desktop app did not have
- Adding pages or screens with no desktop equivalent (confirm with user first)
- Calling external APIs not exposed by your backend
- Adding frontend state management patterns "for scalability" if not needed for functional parity

---

## New File Protocol (Both Modes)

### Mode A — new backend file
1. List every source file you checked
2. Write: `"Verified [X] absent from desktop source. Checked: [files]. Required because: [reason]. Create? Y/N"`
3. Wait for confirmation

> Exception: `Dockerfile`, `docker-compose.yml`, API router file — pre-approved `[PT]` infrastructure.

### Mode B — new frontend file
Frontend files are expected to be new. Still print pre-flight. Still confirm the desktop equivalent you are mirroring.

---

## Impossible Port Protocol (Mode A only)

If a desktop backend feature has no Docker/web equivalent:

1. Do not invent an alternative
2. Write: `"CANNOT PORT: [feature] — [reason]. Options: [source-faithful options only]"`
3. Wait for user decision

---

## Per-File Output Format

```
[PRE-FLIGHT BLOCK]

[CODE]

DIFF SUMMARY (Mode A):
- [PT] Changed: [desktop thing] → [web/docker thing] (reason: [PT reason])
- Added API endpoint: [route] → wraps [existing function]
- Added: nothing else

DIFF SUMMARY (Mode B):
- Mirrors: [desktop screen/feature]
- Functionality covered: [list]
- API calls: [list endpoints]
```

---

## Checklist (Run Before Submitting Every File)

**Mode A:**
- [ ] Read desktop source equivalent
- [ ] Quoted source in pre-flight
- [ ] Every change labeled `[PT]` or blocked as `[ED]`
- [ ] API endpoints only wrap existing functions
- [ ] No logic changes, renames, or additions
- [ ] Ask: *"Would the original desktop developer recognize this backend logic?"*

**Mode B:**
- [ ] Identified desktop equivalent being mirrored
- [ ] Listed functionality being replicated
- [ ] No features added beyond desktop equivalent
- [ ] API calls go to backend endpoints only
- [ ] Ask: *"Can the user do everything here they could do in the desktop app?"*

---

## Forbidden Phrases (Both Modes)

- "I'll create a better version..."
- "Let me improve this by..."
- "A cleaner approach would be..."
- "We should also add..."
- "While I'm at it..."
- "Since we're moving to web, we can take advantage of..."
- "For scalability, I'll..."
- "This is a good opportunity to..."
- "I'll also handle the case where..."

---

## Allowed Phrases

- `"MODE A — Porting exact logic from [source-file]..."`
- `"MODE B — Mirroring [desktop feature]. No desktop code being ported."`
- `"[PT] [desktop thing] → [web equivalent] because [reason]"`
- `"CANNOT PORT: [X]. Options:"`
- `"Verified [X] absent from desktop source. Checked: [files]. Create? Y/N"`
- `"DRIFT DETECTED: I was about to [action]. Reverting."`
- `"BOUNDARY VIOLATION: I was about to share [X] across frontend/backend. Options:"`

---

## Violation Hierarchy

| Severity | Violation | Response |
|---|---|---|
| 🔴 Critical | Code output without pre-flight block | Discard, restart with pre-flight |
| 🔴 Critical | Mode A: backend logic changed, not just wrapped | Discard, revert to source |
| 🔴 Critical | Mode B: features added with no desktop equivalent | Remove additions, confirm with user |
| 🔴 Critical | Frontend imports backend code directly | Remove import, use API endpoint instead |
| 🔴 Critical | Shared module created spanning frontend and backend | Flag boundary violation, ask user |
| 🔴 Critical | API contract changed without user confirmation | Revert route/shape/status to prior contract |
| 🟠 High | Mode A: variable/function renamed | Flag, revert |
| 🟠 High | Mode A: new API endpoint not backed by existing function | Remove endpoint |
| 🟠 High | Backend response shaped for a specific frontend component | Generalize response shape |
| 🟡 Medium | Mode A: error handling added not in source | Remove, note in diff |
| 🟡 Medium | Mode B: UI component added with no desktop equivalent | Flag, confirm with user |

---

## Honest Limitations

This protocol reduces AI drift significantly. It does not eliminate it. You should:

1. **Review every Mode A diff** — backend files that grew substantially without documented `[PT]` changes are a red flag
2. **Review every Mode B pre-flight** — if the desktop equivalent listed is vague, the frontend may be inventing features
3. **Audit API endpoints** — each one should map to a named existing function in the backend source
4. **Test functional parity** — can you do everything in the web UI that you could do in the desktop app? Nothing more, nothing less.

---

## Summary

```
Identify mode → Check separation contract → Print pre-flight →
  Mode A: Read source → Quote source → Port syntax → Wrap with API → List changes → Stop
  Mode B: Identify desktop equivalent → Mirror functionality → Call backend API → List coverage → Stop

Always: Frontend and backend never share code. API is the only crossing point.
```

If the action is not in the relevant mode's steps, do not do it.

---

## Change Tracking — Required

You must maintain a file called `PORTING_LOG.md` in the project root. This file is your source of truth for the entire porting effort. Update it after every file you touch — not at the end of a session, after every file.

### PORTING_LOG.md Structure

```markdown
# Porting Log
## Project: [desktop app name] → Docker + Web UI
## Desktop Source Version: [version or commit hash if known]
## Last Updated: [date]

---

## API Contract
<!-- Every defined endpoint. Treat this as frozen once written. -->

| Method | Route | Wraps | Request | Response Shape | Status |
|--------|-------|-------|---------|----------------|--------|
| GET | /api/... | backendFn() | - | { field: type } | stable |

---

## Status Summary
- Total backend files identified: X
- Backend ported: X / X
- Frontend screens identified: X
- Frontend built: X / X
- Blocked items: X

---

## Completed

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| [output file] | [source file : lines] | [PT changes only] | [date] |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| [component] | [desktop screen/feature] | [list] | [date] |

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Dockerfile | ... | [date] |
| docker-compose.yml | ... | [date] |

---

## In Progress
| File | Mode | Blocker |
|------|------|---------|
| [file] | A/B | [what's blocking if anything] |

---

## Remaining
### Backend Files To Port
- [ ] [source file] → [target file]
- [ ] ...

### Frontend Screens To Build
- [ ] [desktop screen] → [web equivalent]
- [ ] ...

### Infrastructure
- [ ] [item]

---

## Deferred / Needs Decision
| Item | Reason | Options | Status |
|------|--------|---------|--------|
| [feature] | CANNOT PORT: [reason] | [options given to user] | awaiting user |

---

## API Contract Change Log
<!-- Any time an endpoint is added or modified, log it here -->
| Date | Change | Reason | Approved By |
|------|--------|--------|-------------|
| [date] | Added GET /api/x | New feature port | user confirmed |

---

## Future Update Notes
<!-- Notes to help port future desktop updates -->
| Desktop Component | Docker Equivalent | Notes for Future Updates |
|-------------------|-------------------|--------------------------|
| [desktop file] | [docker file] | [anything non-obvious about how they map] |
```

### Change Tracking Rules

**Update PORTING_LOG.md after every file.** Not at end of session. After every file.

**Before starting any task**, read PORTING_LOG.md and state:
```
LOG CHECK:
- Last completed: [file]
- Currently in progress: [file or "nothing"]
- Next up: [file]
- Open decisions: [list or "none"]
```

**API Contract section is append-only.** Never remove a row. If an endpoint changes, add a new row and note the change. The change log at the bottom must reflect every modification.

**Future Update Notes section** is the most important section for your long-term goal. Every time you port a backend file, add a row explaining how the desktop source file maps to the Docker equivalent — including anything non-obvious. When the desktop app updates, this section tells you exactly where to look.

**Never mark something Complete if:**
- The pre-flight block was skipped
- The diff summary is missing
- A deferred decision is still open for that file

### PORTING_LOG.md is a Protected File

- Do not reformat or restructure it
- Do not delete rows — mark them [superseded] if needed
- Do not summarize away detail — every file gets its own row
- If PORTING_LOG.md does not exist yet, create it before writing any other file