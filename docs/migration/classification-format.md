# Classification hierarchy exchange format (`keel-classification/1`)

Used to load a classification hierarchy (for example from Teamcenter classification export) and to move one between environments.
Service: `ClassificationService.ImportHierarchyAsync` / `ExportHierarchyAsync` in `Keel.Admin`; API: `GET /api/v1/admin/classification/export?format=json|csv`,
`POST /api/v1/admin/classification/import?format=json|csv&dryRun=true|false&reason=...` (plm-admin only); UI: Administration, Classification.

## Migration {#migration}
`keel-import` reads the same JSON as the optional file `classification.json` at the root of a package (any source format), so a Teamcenter or Windchill classification export converted to this format travels with the package and is covered by its hash. With `classification.load` the importer creates the classes that Keel lacks (it uses the import code below, restricted to missing classes: **existing classes are never updated**; a different parent or attribute type is reported as a conflict, other differences as warnings) and writes the class and values of each part onto its iterations. A class with status `retired` in the file is not created. Class assignments themselves come from the source's classification rows (`UserData type="Classification"` in PLM XML, `classifications.json|csv` in Windchill, `classifications` in the canonical package). See [mapping-reference.md](mapping-reference.md#classification).

## Semantics
- Classes are identified by **code** (case-insensitive, `[A-Za-z0-9][A-Za-z0-9._-]{0,39}`), attributes by **name** within a class (`[a-z][a-z0-9_]{0,62}`).
- Import creates missing classes (parents before children, any order in the file) and updates name, description, sort order and attributes of existing ones.
- Import never deletes, moves or retires. A file that puts an existing class under another parent, or changes its status, is reported with an error that names the checked operation (Move, Retire, Restore).
- Attribute names must be unique along a path. Updates are checked against already classified parts; any error applies nothing.
- A dry run (default for the API) runs the same code and rolls back; its report equals what the apply does.

## JSON
```json
{
  "format": "keel-classification/1",
  "nodes": [
    { "code": "MECH", "name": "Mechanical", "description": "", "parent": null, "sortOrder": 10, "status": "active",
      "attributes": [ { "name": "grade", "label": "Material grade", "dataType": "enum", "enumValues": ["304", "316"], "required": false, "sortOrder": 10, "group": "Material" } ] },
    { "code": "FAST", "name": "Fasteners", "parent": "MECH", "sortOrder": 10, "status": "active",
      "attributes": [ { "name": "diameter", "label": "Diameter", "dataType": "number", "unit": "mm", "min": 1, "max": 100, "required": true } ] }
  ]
}
```
Attribute fields: `name`, `label`, `dataType` (`string|number|boolean|enum`), `unit` (numbers), `enumValues`, `required`, `maxLength` (text), `pattern` (regex for the whole value, text), `min`, `max` (numbers), `helpText`, `group`, `sortOrder`, `retired`.

## CSV
RFC 4180 (quotes, commas and line breaks inside quoted fields). One header row, then a `class` row per class followed by `attribute` rows that name the class in `code`:

`record,code,parent,name,description,sort_order,status,attr_name,attr_label,data_type,unit,enum_values,required,max_length,pattern,min,max,help_text,group,attr_sort,retired`

`class` rows use code, parent, name, description, sort_order, status. `attribute` rows use code (the class), attr_* columns and the validation columns; enum values are separated by `|`; booleans are `true`/`false`.
