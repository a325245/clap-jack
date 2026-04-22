# Copilot Instructions

## Project Guidelines
- Player view must be chat-driven only (no internal engine state), with gameplay updates mirrored from chat output/parsing.
- The Unknown-world tell issue is triggered by the Add Party flow, not the Add Player flow.
- Prefer modular fixes and reusable solutions because patterns here will matter across other games too.
- Player configuration should expose card display options only (size/style/theme).
- Dealer and player views must utilize the top Table/Config tabs.
- Global bet limits and AFK workflows are to be treated as first-class UX elements.
- Ensure chat-visible confirmations for critical dealer actions, such as bank updates.
- For Hold'em UX, dealer view should support a config toggle to show/hide other players' hands (default on), while player view must remain chat-driven and only mirror information parsed from chat output.
- Default natural chat output should be enabled; in streamer mode, hide game-related dealer UI fields (bet amount/inputs), hide identity-sensitive controls, and keep only necessary command controls and non-announcement obscured chat.
