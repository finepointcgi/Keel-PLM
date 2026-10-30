# 3. Parts

A **part** is an item you design, make or buy. It has a **master** (number and name, shared by all versions), **revisions** (A, B, C... formal, change-controlled versions) and **iterations** (A.1, A.2... working saves inside a revision). The lifecycle state lives on the revision.

## Find parts

1. Select **Parts** in the rail.
2. Type in the search box. The list matches numbers and names (full-text and fuzzy). Use the filters to narrow by type and state, and select a column heading to sort the rows on the page.
3. Each row has a marking chip. The banner at the top and bottom of the page is the **aggregate** of the rows shown.

![The parts list with marking chips and the aggregate banner](images/parts-list.png)

Parts you may not read are not in the list. **Parts > classification** (`/parts/classification`) finds parts by class and class attributes (for example wall thickness).

![Searching by classification](images/parts-classification-search.png)

## Create a part

1. On the parts list select **New part**.
2. Fill in the name, type, unit, source (Make or Buy) and the attributes your administrator defined. The part number is assigned automatically (`P-nnnnnn`); only administrators can choose one.
3. Set the **markings** (export jurisdiction, CUI categories, distribution statement, program and owning organization). They are mandatory and validated: an ITAR part needs a USML category, export-controlled data must be CUI//EXPT, and it cannot have distribution A.
4. Select **Create part**.

![The new part form](images/parts-new.png)

## Read a part

The part page has these tabs:

| Tab | What it shows |
|---|---|
| Overview | The version, its description and attributes |
| Structure | The bill of materials ([chapter 4](04-boms-variants-mbom.md)) |
| Documents | Linked documents ([chapter 5](05-documents-and-vault.md)) |
| Changes | Change notices that affect the part ([chapter 7](07-change-management.md)) |
| Review & release | The review workflow and its history ([chapter 6](06-lifecycles-workflow-signatures.md)) |
| Classification | Class and class attribute values |
| History | Every version, with who and when |
| Timeline | Events in time order, including lifecycle, change and workflow entries |
| Security markings | The markings and the history of changes to them |
| Sourcing, Quality, Compliance, Trace | Other areas' data for this part ([chapters 10, 12, 14, 15](10-quality.md)) |
| Activity, Comments | Who did what; discussion ([chapter 19](19-notifications-and-activity.md)) |

![The Overview tab of the gimbal bracket](images/parts-overview.png)

Use the revision links under the stepper (for example **Rev A (Released)**, **Rev B (In Work)**) to switch revisions.

![The History tab](images/parts-history-tab.png)
![The Timeline tab](images/parts-timeline-tab.png)
![The Changes tab lists change notices affecting the part](images/parts-changes-tab.png)
![The Classification tab](images/parts-classification-tab.png)
![The Trace tab lists allocated requirements](images/parts-trace-tab.png)

## Change a part: check out, edit, check in

A part is changed only by its holder. **Check out** takes an exclusive lock and creates your private working copy.

1. On an In Work part select **Check out**.
2. Edit the description and attributes (and, on assemblies, the BOM) in the working copy.
3. Select **Save working copy** as often as you like.
4. Select **Check in**. This creates the next iteration (A.1 becomes A.2). Document links and BOM lines are copied forward.
5. Changed your mind? **Undo check-out** discards the working copy. Only the holder or an administrator can undo it.

Released revisions are locked. To change a released part you revise it inside a change notice ([chapter 7](07-change-management.md)).

## Move through the lifecycle

The stepper shows In Work, Under Review, Released and Obsolete. The action bar offers only the moves you may make:

- **Move to Under Review** and **Released** happen through an approved review ([chapter 6](06-lifecycles-workflow-signatures.md)); the page tells you so.
- **Move to Obsolete** happens only through a released change notice.
- An administrator can **Set state...** as a documented override with a reason (used for migration and corrections); it is audited.

## Change markings

On the **Security markings** tab (In Work only), choose new markings and give a reason. You cannot lock yourself out of your own part. Markings of locked revisions change only through change control.

![The Security markings tab](images/parts-security-tab.png)

## Favorites, watch and comments

Select **Add to favorites** to pin the part to Home. Select **Watch** to be told about changes ([chapter 19](19-notifications-and-activity.md)).

## Where used and compare

- **Where used** lists the assemblies that use the part, filtered by what you may read ([chapter 4](04-boms-variants-mbom.md)).
- **Compare** shows two versions of the part side by side.

## What is not editable

The name, type, unit and source of a part are fixed once it exists. Renaming is not shipped; description and attributes are per iteration.
