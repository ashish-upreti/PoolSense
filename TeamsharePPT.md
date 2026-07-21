# PoolSense — Team Share Presentation Prompts

Use the following prompts to generate a short 3-slide presentation for teams currently using the Pool system at https://pool.intel.com/. The goal is to introduce PoolSense and show teams how to enable it for their project directly from inside the Pool application.

Each prompt can be pasted into a presentation-capable AI model to generate one slide at a time.

Note: Keep the tone friendly, practical, and non-technical. The audience is Pool system users — not engineers or leadership.

---

## Slide 1 — Introducing PoolSense

**Prompt**

Create a clean, friendly announcement slide introducing `PoolSense` to teams who use the Pool system at https://pool.intel.com/.

Include:
- Title: `Introducing PoolSense`
- Subtitle: `AI-Powered Recommendations, Right in Your Pool Email`
- A short one-paragraph description:
  - `PoolSense is a new AI assistant built on top of the Pool system. When a new ticket is created in your project, PoolSense automatically analyzes it against years of historical pool ticket resolutions and sends a recommendation email — including a suggested root cause, resolution, and links to similar past incidents — directly to your lifeguard or configured recipient.`
- A highlight box with 3 key points:
  - `No new tools to learn — recommendations arrive in your existing pool email`
  - `Powered by your own project's ticket history`
  - `Takes less than a minute to enable`

Style guidance:
- friendly and approachable, not overly technical
- blue, teal, and white palette consistent with Intel corporate style
- suitable for an email attachment or Teams message

---

## Slide 2 — What PoolSense Does

**Prompt**

Create a slide called `What PoolSense Does` for an audience of Pool system users.

Show a simple 3-step flow with icons or numbered steps:

**Step 1 — A new ticket is created in your pool project**
- PoolSense detects new tickets automatically (no action needed from you)

**Step 2 — AI analyzes the ticket**
- PoolSense searches years of historical closed tickets from your project
- 5 AI agents identify the likely root cause, best-matching past incidents, and a suggested resolution
- A confidence score is calculated based on how closely the new ticket matches historical patterns

**Step 3 — A PoolSense link appears in your existing Pool email**
- No separate email is sent — PoolSense adds a link directly inside your existing pool ticket notification email
- Clicking the link opens the PoolSense app pre-loaded with that specific ticket's full analysis:
  - Suggested root cause
  - Suggested resolution
  - Confidence score (0–100%)
  - Most similar past incidents with links
  - Failure pattern classification (system, component, failure type)
- From the PoolSense app, your team can explore further, ask follow-up questions, and troubleshoot interactively

Add a callout:
- `No new email, no new inbox to check — PoolSense is right there in the email you already receive. One click opens the full analysis.`

Style:
- clean 3-column or 3-row layout with icons
- straightforward and visual
- non-technical language throughout

---

## Slide 3 — How to Enable PoolSense for Your Project

**Prompt**

Create a simple how-to slide called `How to Enable PoolSense` for Pool system users.

Show numbered steps with clear visuals:

**Step 1 — Go to the Pool system**
- Open https://pool.intel.com/ and sign in

**Step 2 — Open the Applications menu**
- Navigate to your project in the Pool application
- Find the `PoolSense` toggle inside the application settings or applications menu

**Step 3 — Enable the toggle**
- Flip the `PoolSense` toggle to ON for your project
- That's it — your project is now configured in PoolSense

**What happens next (automatic):**
- PoolSense begins polling your project's new tickets
- When a new ticket arrives, a recommendation email is sent to your configured lifeguard or email recipient
- No additional setup required

Add a tips section:
- `Your email recipient is pre-configured from your pool project settings. Contact the PoolSense team if you need to update it.`
- `You can disable PoolSense at any time by toggling it off in the same menu.`
- `Recommendations are based on your project's own ticket history — the more closed tickets in your history, the better the suggestions.`

Add a closing call to action:
- `Questions? Reach out to the PoolSense team or reply to this email.`

Style:
- step-by-step visual layout (numbered boxes or a simple flow)
- bold the key actions
- friendly, reassuring tone — make it feel easy and low-risk to try

---

## Slide 4 — What We Ask From Scrum Teams

**Prompt**

Create a practical alignment slide called `What We Ask From Scrum Teams` for Pool system users.

Goal of this slide:
- explain that PoolSense quality depends on ticket closure quality
- request better closure discipline from teams so AI recommendations stay accurate and useful

Include a clear message:
- `Teams can collaborate in email as usual. But before closing a ticket, please record a proper root cause and a clear resolution in the ticket itself.`

Add a `Why this matters` section:
- `PoolSense learns from historical closed tickets.`
- `If closures only say "issue fixed" or "closed" without details, AI cannot learn the real pattern.`
- `Good closure notes improve recommendation quality for all teams.`

Add a `Please Do` checklist:
- `Root Cause:` what actually caused the issue (not just symptom)
- `Resolution:` what was changed/fixed
- `Validation:` how you confirmed the issue is resolved
- `Context:` any key dependency, config, job, or component involved

Add a `Please Avoid` section with examples:
- `"Issue fixed"`
- `"Closed"`
- `"Resolved"`
- `"Done"`

Add a sample closure format box:
- `Root Cause: <specific cause>`
- `Resolution: <exact action taken>`
- `Validation: <how verified>`
- `Notes: <optional additional context>`

End with a team message:
- `Better ticket closure details today = better PoolSense recommendations tomorrow.`

Style:
- simple and action-oriented
- supportive tone (not policing)
- easy-to-scan checklist layout

---

## Slide 5 — Additional Help: Improving PoolSense With Feedback

**Prompt**

Create a practical follow-up slide called `Additional Help: Improving PoolSense With Feedback` for Pool system users.

Goal of this slide:
- explain how teams can improve PoolSense recommendations from the troubleshooting screen
- show that Helpful / Not Helpful feedback and comments make future recommendations more accurate
- encourage lifeguards to add the confirmed cause or exact fix after troubleshooting

Include a clear message:
- `After using a PoolSense recommendation, please take a few seconds to tell PoolSense whether it helped and what actually fixed the current pool issue.`

Add a `How feedback works` section with 3 simple steps:

**Step 1 — Select the related historical incident**
- Pick the similar past incident that influenced the recommendation
- This tells PoolSense which historical example should receive the feedback impact

**Step 2 — Choose Helpful or Not Helpful**
- `Helpful` means the selected incident was relevant and should be trusted more for similar future issues
- `Not Helpful` means the selected incident looked similar but led to the wrong path, so PoolSense should be more careful next time

**Step 3 — Add the current pool cause or exact fix**
- Enter what actually caused the current issue
- Enter the exact action taken to resolve it
- If you used the recommendation, check `I used this resolution`

Add a `Why this matters` section:
- `Helpful feedback teaches PoolSense which past incidents are reliable.`
- `Not Helpful feedback teaches PoolSense what paths to avoid.`
- `Confirmed cause/fix comments become high-trust evidence for future similar issues.`
- `The more specific the feedback, the better PoolSense can help the next lifeguard.`

Add examples:

**Good Helpful Feedback**
- `Confirmed root cause: stale VG mapping after DataLoad failure. Reran DataLoad and refreshed VG mapping; pool validated successfully.`

**Good Not Helpful Feedback**
- `This was not a VG mapping issue. Actual cause was missing application filter in project configuration.`

Add a closing team message:
- `Every feedback note makes PoolSense smarter for the next similar ticket.`

Style:
- friendly and non-technical
- simple 3-step visual layout
- include a small feedback loop graphic if possible
- supportive tone, focused on helping teams improve recommendation quality