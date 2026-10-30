# ADR-0049: Program resources and cost loading: pools, roles, assignments, load, budget against actual against estimate to complete

Status: accepted (increment 9.5, 2026-10-30). Builds on ADR-0004 (markings and access policy), ADR-0005 (tamper-evident audit), ADR-0019 (marking dominance), ADR-0029 and ADR-0043
(programs, the visuals kit), ADR-0030 (activity feed), ADR-0031 (sort whitelists) and ADR-0044 (Excel and CSV output).
Assumptions are A58 to A60 in [../assumptions.md](../assumptions.md), detailed as FEAT-RES-n in [increments/9.5-program-resources.md](../increments/9.5-program-resources.md).
Interface: `docs/programs/openapi.json`.

## Context
ADR-0043 gave a program a plan: phases, tasks, milestones, dependencies and deliverables. A program manager also needs to know who does the work, whether anyone is booked
beyond what they can do, what the plan costs, and whether the program will finish inside its budget. That is money and staffing data. Rates and budgets are among the most sensitive
numbers of a program, and a roll-up that sums rows is the classic way to leak the rows it was built from.

## Decisions

### 1. Everything belongs to one program and is marked like the rest of the program
`Keel.Programs` (migration `0028_program_resources.sql`, schema `prg`) adds pools, roles, resources, assignments, budget lines, actual costs and material lines. Each table has the marking
columns of ADR-0004 (a database test counts them) and its markings must cover the program's and those of what the row belongs to (ADR-0019): a role covers its pool, a resource its role, an
assignment both the task and the resource. Reading is the ordinary SQL read filter per table, and **every number is computed over the rows the reader may read**: hours, over-allocation, cost
totals and the banner of the page. A row the reader cannot read is not a placeholder and not a count; it is absent. Rates are child rows of a role (effective-dated, append-only), read only
through readable roles. A resource is a name and a capacity, not a person: nothing here reads or writes membership or the user directory, so naming a resource grants nobody anything.

### 2. A second gate for money: the program cost permission
Marking decides which rows a person may read; it does not say who may see money. The role `plm-program-cost` (`Roles.ProgramCost`, Keycloak realm role) is an additional condition for **every
rate and every amount**: the cost view and export tables, the budget, actual costs, material lines and rates in the resource catalog. Without it a member of the program still sees resources,
assignments, hours, load and over-allocation, with the rate column absent and no cost tab. The refusal is a 403 with the reason code `cost.role_required` after the usual 404 for a program the
caller cannot read (existence is never disclosed), and it is audited as a denial. The role is not in `Roles.All` and grants no `PlmAction`; administrators and auditors do not hold it by default
(separation of duties: the person who administers the system or reads its audit log does not thereby read program money). Changing money additionally needs Modify on the program's markings.

### 3. Pure arithmetic, no clock
`ResourceMath` is deterministic arithmetic over plain inputs (tested for input-order independence). A day of work is a weekday; an assignment is hours per day over a date range; several
assignments to one resource add up. A resource is over-allocated on a day when the hours exceed its capacity (zero outside its availability window); consecutive working days of
over-allocation form one period. Rates are a history: the rate on a day is the latest one on or before it, and hours with no rate are counted and reported, never priced at zero silently.
Cost: **planned** is the full loading of every assignment; **actual** is the recorded entries; **estimate to complete** (ETC) is, for labor, the share of the assigned hours not yet done
(`100 - percent complete` of the task) spread over the working days after the status date (on the next working day for late work), for material the estimate of lines needed after the status
date; estimate at completion (EAC) is actual plus ETC and the variance is budget minus EAC. The status date is an input (default today).

### 4. Recorded costs are immutable
Actual costs, rates and the budget log are never updated or deleted (triggers; the application role has no UPDATE or DELETE). A correction is a reversing entry that names the one it cancels
(a trigger checks the same program, type and opposite amount; one reversal per entry). Labor is entered as hours and a resource and priced by the module at the rate in force that day, so a
person cannot book a labor amount that does not follow from a rate. A budget amount can change, and every change is written to the budget log.

### 5. Material cost is read live from the product structure and the price lists, through a port
A material line is a quantity of an end item (a part number) needed on a date. Its cost is **estimated at read time** by `IProgramMaterialSource` (Keel.Core), implemented by the host over the
BOM and sourcing services, so `Keel.Programs` references neither (the module-boundary test keeps it so) and no price is ever stored in the program. The implementation explodes the latest
released structure with the BOM service's own read-filtered explosion, prices leaf parts from active, effective, USD entries of the sourcing lists (a preferred entry before an approved one,
cheapest price break row that fits the quantity; alternates and disapproved entries never), and reports parts it could not price and structure lines the reader may not read, which are in no
total. The same program therefore costs less to a reader who cannot read an ITAR part than to one who can, and the banner follows what went in.

### 6. Where money is kept out
Cost operations are audited with keys, types and counts, never amounts (an architecture test rejects a detail key that could hold an amount or text). The money actions are classified
**Hidden** in `ActivityCatalog`, so the activity feed that every member of the program reads does not carry them; resource and assignment changes are normal feed events (hours only).
Cost reads are views, audited. Exports (Excel with every table, CSV with one) are built by the host from the same reads: a reader without the permission gets the hours tables only, the file
starts with the aggregate banner of what it holds and the currency, text cells are neutralized against formula injection, numbers are numbers, and the export itself is audited.

### 7. Pictures with tables
The visuals kit gains two pictures, both server-computed SVG with no script and no outside library: the cumulative cost curves (budget, actual, EAC: a line style **and** a marker shape each,
the last values written on the chart, a spoken summary) and the weekly load picture (one keyboard stop per resource in the pan-and-zoom frame, bands none, up to half, up to full, and over
capacity drawn hatched with a heavy outline). Each has a table view with the same facts, and the legend names every shape.

## Consequences
- One currency (USD) and weekday arithmetic are deliberate limits (A59, A60). Pools are per program, so the same crew in two programs is entered twice; this is what keeps a rate card from
  becoming a cross-program leak (A58).
- Over-allocation is judged inside one program over the rows the reader can read. A person booked in two programs is not combined, because combining would need rows of programs the reader
  may not be in.
- Material estimates depend on sourcing data being present and priced in USD; where it is not, the estimate says so instead of guessing.
