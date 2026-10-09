# Real-Time Meeting Companion — Implementation Pack

This pack is designed for independent AI coding agents to implement and verify the first Windows MVP.

**Read in this order:**

1. `IMPLEMENTATION_PLAN.md` — product spec, architecture, contracts, milestones, validation matrix, documentation links.
2. `AGENTS.md` — collaboration and non-regression rules for coding agents.
3. `AGENT_BACKLOG.json` — machine-readable 17-ticket implementation queue with dependencies and acceptance gates.

**Status:** These are planning and delegation artifacts, not the executable application. No work has been performed on the user's GitHub repositories.

**Priority compatibility:** Zoom desktop meetings + 1:1 calls; Microsoft Teams desktop meetings + 1:1 calls. Then other Windows apps/browser fallback.

**MVP features:** separate mic and incoming call audio, dual real-time transcription, proactive text-only suggestions, one-click/hotkey screenshot analyzed with recent call context, pause/stop and fault recovery.

**Start with:** `MC-001` foundation and `MC-002/MC-004` Windows audio feasibility. Do not assume process PID names or AI API compatibility without verification.
