# Product Plan — Phases 12 and 13

**Owner:** Product · **Written:** 2026-08-16 · **Status:** proposal, nothing committed

This is a **business plan, not a phase doc**. There are no tasks, no exit criteria and no
verification steps in it — those belong in `docs/phases/PHASE-N-*.md` and should be written only
once a phase is actually chosen. What this file decides is *whether and in what order* to build
Phases 12 and 13 at all, and what has to be true first.

Read [`DECISIONS.md`](../DECISIONS.md) for why the product is shaped the way it is, and
[`ROADMAP.md`](ROADMAP.md) for what is built.

---

## 1. Where the product actually is

**Phases 0–10 are complete.** A shop can sell, take cash, reconcile a drawer, print a receipt, run
a Z-report, keep trading with the network down, and — since 2026-08-15 — run as a restaurant:
tables, courses, modifiers, a kitchen display, split bills and tips.

Three facts matter more than that list:

1. **There is no paying client.** Not one. Every feature decision so far has been made against an
   imagined shop, informed by one real one.
2. **Nobody who works a till or a pass has used it.** This has been the stated top risk since
   Phase 8 and it is still open. The restaurant screens in particular are a set of informed
   guesses about how a room works.
3. **What is deployed is a development environment, by decision** (`DECISIONS.md`, 2026-08-11).
   The API sleeps after 15 minutes; the database deletes itself on 2026-09-10 with no automatic
   backups. This was chosen deliberately and is fine for demos — and it means **there is a piece
   of unbudgeted work between here and any customer** (see §3).

**The product is feature-rich and evidence-poor.** That asymmetry is what this plan exists to
correct, and it is the reason the recommendation below is *not* "build Phase 12".

## 2. The gate everything is behind

**One unresolved decision now blocks the commercial path, by the project's own terms.**

`DECISIONS.md` records the pricing model as unresolved, with an explicit expiry: *"this can stay
open until Phase 10"*. Phase 10 closed on 2026-08-15. The tension is stated there and has never
been resolved:

> "sell as a whole product, not a per-seat license" (leaning one-time purchase)
> versus
> "we host it" (a recurring, per-tenant cost that never stops)

**This is not a technical problem and cannot be solved by building anything.** It is a choice
about what the business is. Three shapes are viable and they are genuinely different businesses:

| Model | What it means | The risk it carries |
|---|---|---|
| **One-time fee, hosting included** | Closest to the original intent. Simplest to sell to a small shop that hates subscriptions | **Every sale creates a permanent liability.** Hosting cost per tenant runs forever against revenue booked once. The tenth customer is cheaper than the first; the hundredth is a business with negative marginal margin |
| **One-time fee + annual hosting** | Splits the two honestly. The software is bought; the hosting is rented | Two line items to explain. A shop that stops paying hosting has bought software it cannot run — that needs an answer before it is sold, not after |
| **Recurring, no purchase** | Matches the cost structure exactly. Standard for the category | Contradicts a stated product principle, and small owner-operators genuinely resist it. Also the only model where churn is a metric that matters |

**Recommendation: decide this before anything else, and decide it before quoting a price to
anyone.** It changes what gets built — a recurring model needs billing, dunning and an
entitlement check; a one-time model needs licensing/activation (already flagged in `DECISIONS.md`
as a V4 concern) and a hard answer to "what happens when hosting cost exceeds the fee".

**Nothing in Phase 12 or 13 should start before this is settled**, because both are investments
whose payback depends on which of the three it is.

## 3. The unbudgeted work nobody has planned

Between "feature complete" and "a shop is trading on it" sits work that appears in no phase:

- **Make the environment production-viable.** `DECISIONS.md` names the single change — *"upgrading
  the API instance is the single change that makes this production-viable"* — plus a database that
  does not delete itself and backups that are proven rather than procedural. Small, known, costs
  money rather than time.
- **Onboard a real tenant.** There is no onboarding endpoint by decision; `tools/Pos.Seed onboard`
  is the path, run by an operator. Fine for the first few, not a product.
- **Support the shop.** No platform admin exists (deliberately — `DECISIONS.md` gates it on the
  first paying client). Until then, support means direct database inspection.
- **Fix the one known money-path defect.** `TenderPanel`'s provisional change ignores the tip, so a
  tipped bill reads high until the server answers. Small, and it is in front of a cashier counting
  notes.

**Call this Phase 11.** The number is free — card payments were dropped from it on 2026-07-31 —
and it is a better use of it than leaving a hole in the sequence. It is the only work that is
unambiguously required no matter which of the three pricing models is chosen, and no matter
whether 12 or 13 ever happens.

---

## 4. Phase 12 — Avalonia desktop

**Roadmap line:** *"Same API, durable local DB, real offline."*

### The value hypothesis, stated so it can be wrong

> A shop will not trust a browser with its till, and the PWA's offline story is best-effort in a
> way that costs us deals. A native client with a real local database, and native access to
> receipt printers, cash drawers and scanners, is what makes this sellable to shops that have been
> burned before.

### Why it is weaker than it looks

- **The offline argument is half-spent already.** Phase 9 shipped a PWA that sells with the network
  down and reconciles when it returns. `DECISIONS.md` is honest that browser storage is
  "best effort, not bulletproof" — but the gap between best-effort and durable is narrower than
  the gap between nothing and best-effort, and the second gap is already closed.
- **The real case is hardware, not offline.** The stack was chosen partly *because* the POS
  peripheral ecosystem has strong native .NET support (`DECISIONS.md`, Tech Stack). Receipt
  printers, cash drawers and scanners are where a browser genuinely cannot go — Phase 6.2 already
  records the limitation on real thermal printers. **If Phase 12 is justified, this is why**, and
  the plan should say so instead of leading with offline.
- **Nobody has asked for it.** Zero customers, so zero requests. Building a second client for an
  unvalidated product doubles the surface that every future feature has to ship into.

### What it would cost, in commitment rather than hours

A second client is not a one-off. Every subsequent feature ships twice, is tested twice, and can
diverge. The architecture makes this *cheap* — one API, `Pos.Core` client-agnostic and enforced by
a test — but cheap is not free, and the cost is permanent and recurring, unlike the build.

### Evidence that would justify starting it

- A real shop says the browser is the reason they will not buy, **or**
- A real shop needs a cash drawer or thermal printer that the web app cannot drive, **or**
- A shop loses data to browser storage being cleared, in the field, at least once.

### What would kill it

The first paying shop runs happily on the PWA for a full trading month, including a network
outage, with a USB scanner (which works in a browser — it types) and no drawer or printer
requirement. **Then desktop is a solution to a problem this product does not have**, and the right
move is to say so in `DECISIONS.md` and close it the way card payments were closed.

### Recommendation

**Do not start. Hold pending evidence.** Keep `Pos.Core` and `Pos.Data` client-agnostic — that is
already enforced and costs nothing to maintain — so the option stays open and cheap.

---

## 5. Phase 13 — "Business layer"

**Roadmap line:** *"Platform admin, loyalty, purchase orders, low-stock alerts, gift cards,
analytics."*

### The framing problem

**This is not one phase.** It is six unrelated features with different buyers, different risks and
wildly different costs, bundled under one number. Treating it as a unit guarantees the cheap
valuable thing waits behind the expensive speculative one. **Decompose it and sequence by value,
not by the order the list happens to be written in.**

| # | Feature | Who wants it | Cost | Notes |
|---|---|---|---|---|
| 1 | **Low-stock alerts** | The shop owner, daily | **Smallest** | `StockItem.ReorderPoint` already exists and `GET /stock?belowReorderPoint=` already filters on it. Nothing surfaces it. This is a notification and a screen over data that is already modelled and already correct |
| 2 | **Analytics dashboard** | The owner, weekly | Small–medium | Reports already exist and reconcile. This is presentation over `ReportQueries`, not new truth |
| 3 | **Platform admin** | **Us**, not the customer | Medium | Gated by `DECISIONS.md`: not before the first paying client, so that real operational needs shape it. That gate is correct and should hold |
| 4 | **Purchase orders** | The owner, weekly | Medium–large | Needs a supplier model, receiving against an order, and partial deliveries. Touches the stock ledger, which is invariant-heavy |
| 5 | **Customer / loyalty** | The owner; the customer feels it | Large | New personal-data surface with a GDPR obligation attached. The first feature in this product that stores a member of the public's details |
| 6 | **Gift cards** | The owner | **Largest, and mispriced by intuition** | A gift card is a **liability on the shop's balance sheet**, not a discount. Issued value must reconcile against redeemed value forever, it interacts with refunds and voids, and it is money the shop owes. Money-path work with append-only consequences |

### The observation worth acting on

**Low-stock alerts are close to free and are the thing an owner touches most.** The reorder point
is already on the entity, already filterable, and used today by nothing but a test fixture. A shop
running out of a fast mover on a Saturday is a real, weekly, revenue-losing event, and this is the
cheapest item on the entire post-MVP list.

If any part of Phase 13 is done before a paying client — and it does not have to be — **this is
the part**, because it is small enough to be wrong about cheaply.

### The observation worth resisting

**Gift cards look like a small feature and are not.** They create a durable financial liability,
they must reconcile against redemptions indefinitely, and they interact with every reversal path
the product already has. They belong after there is a real shop with a real accountant, or not at
all.

### Recommendation

**Unbundle Phase 13.** Retire "business layer" as a unit. Re-number as needed and gate each piece
on evidence:

- **Low-stock alerts** — the only candidate for pre-customer work, and only if there is idle time
  that cannot be spent on getting a customer.
- **Platform admin** — hold the existing gate. First paying client, then build what support
  actually turned out to need.
- **Everything else** — hold pending a customer asking, by name.

---

## 6. Recommended sequence

| Order | What | Gate |
|---|---|---|
| **1** | **Resolve the pricing model** | Nothing. Overdue by the project's own deadline |
| **2** | **Phase 11 — production readiness** (§3) | Follows from 1, since the model decides what billing/entitlement needs to exist |
| **3** | **Get one shop trading on it** | Follows from 2 |
| **4** | **Fix what that shop tells us is wrong** | Certain to exist; unplannable until it does |
| **5** | Platform admin | Existing gate: first paying client |
| **6** | Anything from 12 or 13 | Evidence, by the criteria above |

**The uncomfortable part, stated plainly:** steps 1, 3 and 4 are not engineering, and the project's
strong bias is toward building. Ten phases of well-tested software have been delivered against zero
customer evidence. The highest-value work available now is the work that produces evidence, and
none of it involves writing code.

## 7. Open questions this plan cannot answer

1. **Which pricing model?** §2. Blocks everything commercial.
2. **Is there a candidate shop?** `DECISIONS.md` refers to "the shop this is being built for" —
   whether that is a committed first customer or an archetype changes step 3 entirely.
3. **Retail or restaurant first to market?** Phase 10 doubled the addressable market on paper.
   Restaurant is the larger, stickier sale and the more demanding user; retail is proven further.
   Selling to both at once with no reference customer is the weakest option.
4. **What is the support commitment?** A POS is not software a shop can be down on. Nobody has
   decided what is promised, what it costs, or who answers at 8pm on a Saturday.

---

**This plan commits to nothing.** It exists so that starting Phase 12 or 13 is a decision somebody
makes on the evidence, rather than the default that follows from a number being next.
