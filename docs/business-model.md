# How Keel stays free and still pays for itself

Status: a proposal for the owner to decide on. Nothing here is announced, priced or on sale.

## The idea

The software is free to download, run and use, with every feature and no per-seat fee. Money comes from people who want help, not from locking the product. This is the "free software, paid services" model.

## Decide first: the license

The repository has no `LICENSE` file yet. Until it has one, nobody has a clear right to use it, so this comes before any website or price. Options, from most to least open:

| Option | Free to run | Stops a cloud company reselling it | Notes |
|---|---|---|---|
| Apache-2.0 | yes | no | Easiest for adopters; anyone may sell hosting of it. |
| AGPL-3.0 | yes | mostly | Anyone offering it as a hosted service must share their changes. Common for self-hosted business software. Some companies avoid AGPL. |
| Business Source License (source-available) | yes, with a limit (often "not as a competing hosted service") | yes | Converts to open source after a set time. Not "open source" by the strict definition. |

Recommendation: **AGPL-3.0**, with a commercial license offered to companies that cannot use AGPL. It keeps the product free for the people it is meant for and gives a reason to pay without crippling anything. Have a lawyer read it, because Keel handles export-controlled data and customers will ask.

## What people can pay for

1. **Support and updates (annual).** A named contact, response times, upgrade help and security-fix notices. This is the main income for most self-hosted products.
2. **Setup and migration (one-time).** Install in the customer's environment and move parts, documents and history across, with training.
3. **Compliance help.** Evidence packs and configuration guidance mapped to NIST SP 800-171 and CMMC controls. Keel does not claim to be certified; it supports the controls.
4. **Managed hosting.** Only where the customer's rules allow it. Many Keel users cannot, because their data may not leave their site, so this is smaller than for a normal product.
5. **Commercial license** for companies that cannot use AGPL.
6. **Custom work.** Connectors to other systems, special reports, a CAD integration for a specific tool.

Not recommended: charging per seat, or holding back features such as the audit trail or markings. The people who most need those are the ones who will walk away.

## Who pays

Small and mid-size defense suppliers who need PLM and compliance but cannot afford the large vendors. They have budgets for support and audits, and little tolerance for downtime. They find products through other suppliers, so a few good reference customers matter more than advertising.

## First steps

1. Choose and add the license.
2. Put the site (`site/`) on GitHub Pages or any static host, and turn on GitHub Discussions as the place to say "I would pay for this".
3. Offer setup and support to the first few teams at a discount in return for a case study.
4. Price only after talking to three to five real buyers.

## Risks to be honest about

- Selling to defense suppliers means long, careful buying cycles and expectations around security. Plan for questions, questionnaires and a security contact.
- A one-person project promising support needs a plan for being unavailable (a second maintainer, or a clear support window).
- "Keel" is a working name. Check it is free to use as a product name before building a brand on it.
