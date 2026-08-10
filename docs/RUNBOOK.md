# Runbook

Operational procedures for a deployed POS. Written to be followed by somebody who did not
build it, at an hour they did not choose.

> **Read the whole procedure before starting it.** Several of these are irreversible, and
> two of them (restore, and resetting an Owner) are usually run under time pressure with a
> shop unable to trade.

| Scenario | Section |
|---|---|
| Onboard a new shop | [Onboarding a tenant](#onboarding-a-tenant) |
| Somebody is locked out of the Owner account | [Resetting a locked-out Owner](#resetting-a-locked-out-owner) |
| A till was lost or stolen | [Revoking a device](#revoking-a-device) |
| "My total is wrong" | [Investigating a disputed total](#investigating-a-disputed-total) |
| A password or token has leaked | [When a credential leaks](#when-a-credential-leaks) |
| Restore from backup | [Restoring from backup](#restoring-from-backup) |
| Give a shop its data | [Exporting one tenant's data](#exporting-one-tenants-data) |
| A deploy went wrong | [Rolling back a deploy](#rolling-back-a-deploy) |

---

## Onboarding a tenant

One command. There is no onboarding endpoint and no platform admin UI — by decision, until
after the first paying client ([`DECISIONS.md`](../DECISIONS.md)). This is a CLI holding a
database credential, which is what makes "cannot be invoked with a tenant token" true by
construction rather than by a check.

**Before you run it, get two answers from the shop.** Both are effectively permanent:

- **Tax mode.** `Inclusive` means the shelf price already contains the tax — usual for
  retail in Ireland, the UK and the EU. `Exclusive` adds it at the till. `PUT /settings`
  refuses to change this once the shop has a single sale, correctly: changing it would
  reprice history.
- **When their trading day starts.** `00:00` unless they trade past midnight, in which case
  `04:00` is typical — it makes a 02:00 shift close land on the right day. This one cannot
  be changed at all afterwards, because moving it moves every trading-day boundary that has
  already been reported on, and yesterday's Z-report stops matching yesterday.

```bash
# Connect as the APPLICATION role (pos_app), not the owner. If this command can write it,
# the running API can read it — seeding as a superuser bypasses RLS and hides a policy
# mistake until it surfaces as an empty screen.
export POS_SEED_CONNECTION='Host=…;Database=…;Username=pos_app;Password=…'

dotnet run --project tools/Pos.Seed -- onboard \
  --slug harbour-stores \
  --name "Harbour Stores" \
  --tax-mode Inclusive \
  --timezone Europe/Dublin \
  --business-day-start 04:00 \
  --owner-email maeve@harbourstores.ie \
  --owner-name "Maeve Ryan" \
  --address "3 Quay Street, Galway" \
  --tax-number IE9876543X \
  --with-register
```

It prints the tenant id, the Owner's credentials and — with `--with-register` — a device
token. **The password and the device token are shown once and are not recoverable.** Only a
SHA-256 of the token is stored, and there is no password-reset flow yet.

To choose the password yourself, set `POS_ONBOARD_PASSWORD` in the environment. Do not pass
it as a flag: arguments appear in the shell history and in `ps` output for every user on
the machine.

**It refuses an existing slug.** That is deliberate — onboarding is not idempotent, and
continuing would attach a second Owner to a live shop. If you are retrying, find out
whether the first attempt succeeded before running anything else:

```sql
SELECT id, slug, name, created_at FROM tenant WHERE slug = 'harbour-stores';
```

**Verify before handing over.** Sign in as the Owner at the web app, confirm the settings
screen shows the right currency, tax mode and day start, then complete one cash sale and
print its receipt. The receipt is where a wrong address or tax number becomes visible, and
it is much easier to fix before the shop has sales.

## Resetting a locked-out Owner

There are two different problems and they have different fixes. Establish which one first.

### Locked out by failed attempts

Identity locks an account for five minutes after five failures. Usually the answer is to
wait. To clear it immediately:

```sql
UPDATE "AspNetUsers"
SET lockout_end = NULL, access_failed_count = 0
WHERE tenant_id = '<tenant-id>' AND normalized_email = 'MAEVE@HARBOURSTORES.IE';
```

### The password is lost

**There is no self-service password reset** — a known gap, recorded in
[`docs/HANDOFF.md`](HANDOFF.md) and not on any phase's list. An Owner sets an initial
password and cannot change it afterwards.

Until that is built, the procedure is to set a new hash directly. **Do not write a hash by
hand and do not copy one between users** — Identity's hasher is versioned and salted, and a
hand-made value produces an account that silently rejects every password.

The supported way is a second Owner:

1. If the shop has another Owner or a Manager who can still sign in, have them create a new
   Owner at `/admin/employees`. That path sets the password through `UserManager` and is
   the only one that cannot produce an unusable hash.
2. Sign in as the new Owner and deactivate the old one.

If nobody can sign in at all, re-run onboarding into a **scratch database** with the same
Identity configuration, take the generated `password_hash` for a known password, and copy
that column onto the stranded user:

```sql
UPDATE "AspNetUsers"
SET password_hash = '<hash from the scratch database>',
    security_stamp = gen_random_uuid()::text,
    lockout_end = NULL,
    access_failed_count = 0
WHERE tenant_id = '<tenant-id>' AND normalized_email = '<EMAIL>';
```

Changing `security_stamp` is not optional: it is what invalidates sessions issued against
the old password. Then have them sign in and **record that this happened** — a password you
have seen is a password that must be changed, and there is currently no way for them to
change it without repeating this.

## Revoking a device

A till has been lost, stolen, or sold with the shop. A device token is a real credential:
it is one of the two factors that lets a cashier sign in with a four-digit PIN.

**The Owner can do this themselves** at `/admin/tills` — "Revoke". That clears
`device_token_hash`, and the till stops authenticating on its next request. It does not
invalidate access tokens already issued, which last up to fifteen minutes.

If the Owner cannot reach the screen:

```sql
UPDATE register SET device_token_hash = NULL
WHERE tenant_id = '<tenant-id>' AND id = '<register-id>';
```

To find the register:

```sql
SELECT id, name, device_token_hash IS NOT NULL AS enrolled
FROM register WHERE tenant_id = '<tenant-id>' ORDER BY name;
```

**Then consider the PINs.** Whoever has the device also has whatever was written on a note
beside it. If the till was stolen rather than mislaid, reset the staff PINs at
`/admin/employees`.

Re-enrol a replacement from the same screen; the new token is shown once.

## Investigating a disputed total

"My total is wrong" is answerable, and the answer is always in the data rather than in a
recalculation. **Never re-price a historical sale to check it** — `SaleLine` stores the
description, unit price, tax rate and discount as of the moment of sale precisely so that a
later price change cannot rewrite history (CLAUDE.md invariant 5).

1. **Find the sale.** The customer has a receipt with a number on it.

   ```sql
   SELECT id, sale_number, type, status, subtotal, discount_total, tax_total,
          rounding_adjustment, total, completed_at
   FROM sale WHERE tenant_id = '<tenant-id>' AND sale_number = <number>;
   ```

   Or in the UI: **Sales → search by receipt number**.

2. **Read its lines as they were sold.** These are snapshots, not joins to the catalog.

   ```sql
   SELECT line_number, description, quantity, unit_price, tax_rate,
          discount_amount, line_subtotal, line_tax, line_total, is_price_overridden
   FROM sale_line WHERE tenant_id = '<tenant-id>' AND sale_id = '<sale-id>'
   ORDER BY line_number;
   ```

   The arithmetic to check by hand: each line is `quantity × unit_price − discount_amount`,
   tax applied according to the tenant's `tax_mode`, and the sale's `total` is the sum plus
   `rounding_adjustment`. **Rounding happens once, on the amount the customer pays** — not
   per line — so lines that individually look a cent off can still sum correctly.

3. **Read what was changed and by whom.** Every price override and discount is audited.

   ```sql
   SELECT occurred_at, action, actor_display_name, register_id, detail
   FROM audit_entry
   WHERE tenant_id = '<tenant-id>' AND detail::text LIKE '%<sale-id>%'
   ORDER BY occurred_at;
   ```

   Or in the UI: **Admin → Audit log**, filtered by action and trading day. A discount or
   an override carries the manager who authorised it, which is usually the answer.

4. **Check whether it was refunded or voided.** A sale's detail page shows refunds against
   it in both directions. A customer disputing a total sometimes has a receipt for a sale
   that was already partly returned.

If the arithmetic in step 2 does not reconcile, that is a bug and not a support question —
capture the sale id, the tenant id and the audit rows before anything else touches them.

## When a credential leaks

Ordered by how quickly the damage stops.

| Leaked | Do this | How long the exposure lasts |
|---|---|---|
| A **device token** | Revoke the register (above) | Until revoked, plus ≤15 min of issued access tokens |
| A **refresh token** | Have the user sign out, or deactivate and reactivate them | Rotation revokes the whole family on reuse, so the theft is usually self-limiting and loud |
| A **PIN** | Reset it at `/admin/employees` | Immediate |
| An **Owner password** | See [Resetting a locked-out Owner](#resetting-a-locked-out-owner) | **Until manually changed — there is no self-service reset** |
| The **JWT signing key** | `fly secrets set Jwt__SigningKey=…` and redeploy | Every existing access token becomes invalid at once; everybody signs in again |
| The **database password** | Rotate the `pos_app` role's password, update the secret, redeploy | Immediate on redeploy |

**A deactivated user's access token keeps working for up to ~15 minutes.** Refresh tokens
are revoked and `IsActive` is checked at login, PIN entry and `/auth/me`, but not while
validating a JWT. For a genuine compromise, rotating the signing key is what closes the
window immediately, at the cost of signing everybody out.

## Restoring from backup

> **Not yet executed against production.** This section is written from the local drill and
> must be re-run and re-timed against real infrastructure before it can be trusted — see
> [`PHASE-8-deployment.md`](phases/PHASE-8-deployment.md) §8.5. An untested backup is not a
> backup.

**Restore into a scratch database first, always.** Never restore over a live one to "check"
it: if the backup is bad, you now have neither.

```bash
# 1. What is available.
fly postgres list
fly pg backups list --app <pg-app>

# 2. Restore into a NEW database, not the live one.
fly pg backups restore <backup-id> --app <pg-app> --new-name pos-restore-check

# 3. Verify the data actually arrived. Row counts alone are not enough — check that a
#    known recent sale is present WITH its line snapshots, because the lines are what a
#    disputed total is answered from.
psql "$RESTORED_URL" -c "SELECT count(*) FROM tenant;"
psql "$RESTORED_URL" -c "SELECT count(*) FROM sale;"
psql "$RESTORED_URL" -c "
  SELECT s.sale_number, s.total, count(l.id) AS lines
  FROM sale s JOIN sale_line l ON (l.tenant_id, l.sale_id) = (s.tenant_id, s.id)
  WHERE s.tenant_id = '<tenant-id>'
  GROUP BY s.sale_number, s.total
  ORDER BY s.sale_number DESC LIMIT 5;"

# 4. Note how long the whole thing took, and update this section with the number.
```

**Restore time measured:** _not yet measured against production._

When promoting a restore to live, the API must be stopped first — a running instance
against a half-restored schema writes rows that the restore does not know about.

## Exporting one tenant's data

A support request, and in some jurisdictions a legal one. Run as the schema owner, which
sees across tenants; every query is filtered on `tenant_id` by hand because RLS is not
applied to the owner role.

```bash
TENANT='<tenant-id>'
for t in tenant product barcode category tax_class stock_item stock_movement \
         sale sale_line tender shift cash_movement stock_discrepancy audit_entry; do
  psql "$DATABASE_URL" -c "\copy (SELECT * FROM $t WHERE tenant_id = '$TENANT') TO '$t.csv' CSV HEADER"
done

# `tenant` itself has no tenant_id — it IS the tenant list.
psql "$DATABASE_URL" -c "\copy (SELECT * FROM tenant WHERE id = '$TENANT') TO 'tenant.csv' CSV HEADER"
```

**Do not include `AspNetUsers`** without deciding deliberately: it carries password and PIN
hashes. A staff list belongs in the export; the hashes do not.

## Rolling back a deploy

The deploy pipeline builds the image once and deploys it **by digest**, so a rollback is a
redeploy of a known digest rather than a rebuild of a hopefully-identical one.

```bash
fly releases --app <api-app>
fly deploy --app <api-app> --image registry.fly.io/<api-app>:<previous-sha>
```

**Whether the database can come with it depends on the migration.**

- **Additive** (a new table, a new nullable column): the previous image runs against the
  new schema. Redeploy and stop.
- **Destructive** (a dropped column, a narrowed type): the previous image cannot run
  against the new schema, and the data the migration removed is gone. This is a restore,
  not a rollback — which is the whole reason the restore drill above has to be real.

Destructive migrations need deliberate sign-off at review time, precisely so this situation
is chosen rather than discovered.
