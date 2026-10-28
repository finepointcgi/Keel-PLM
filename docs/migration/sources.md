# Public sources consulted

No proprietary text was copied. Only the public *structure* of the formats was used to shape the subsets we specify ourselves. Anything not listed here is our own design or assumption.

## Siemens PLM XML
* Schema documentation (element index: Product, ProductRevision, ProductRevisionView, ProductInstance, Occurrence, UserData, DataSet ...): https://www.plm.automation.siemens.com/legacy/products/open/plmxml/docs/v5/documentation/plmxml.html and the v4 equivalent https://www.plm.automation.siemens.com/legacy/products/open/plmxml/docs/v4/documentation/plmxml.html
* Version 7.0.3 element pages (for example the `Part` element, a legacy alias of `ProductRevisionView`): https://media.plm.automation.siemens.com/open/plm-xml/docs/v7.0.3/documentation/plmxml_Part.html
* Teamcenter export of an assembly to PLM XML (transfer mode, revision rule, output of an XML file, a log and a folder of dataset files): https://support.sw.siemens.com/en-US/okba/PL8734991/How-to-Export-NX-Assembly-using-PLMXML-from-Teamcenter/index.html
* Product overview: https://plm.sw.siemens.com/en-US/plm-components/plm-xml/

* Effectivity types (read for the effectivity support): the abstract `Effectivity` element and `EffectivityType` (attributes `id`, `name`, `contextRef`; `contextRef` limits an effectivity to a Structure, StructureRevision or ConfigurationContext): https://media.plm.automation.siemens.com/open/plm-xml/docs/v7.0.3/documentation/plmxml_EffectivityType.html and https://media.plm.automation.siemens.com/open/plm-xml/docs/v7.0.3/documentation/plmxml_Effectivity.html
* `SerialNumberEffectivityType` (`start`, `end` strings; `endCondition` stockOut or openEnded): https://media.plm.automation.siemens.com/open/plm-xml/docs/v7.0.3/documentation/plmxml_SerialNumberEffectivityType.html; `DateEffectivityType` (`start`, `end` as `xsd:dateTime`, `endCondition`): https://media.plm.automation.siemens.com/open/plm-xml/docs/v7.0.2/documentation/plmxml_DateEffectivityType.html; `LotEffectivityType` is named as a derived type of `EffectivityType` (its own attributes were not found); `ReleaseStatusType` carries `effectivityRefs` and `dateReleased`: https://media.plm.automation.siemens.com/open/plm-xml/docs/v7.0.3/documentation/plmxml_ReleaseStatusType.html

What this did and did not establish: the element names and the general shape (master Product, ProductRevision with `masterRef` and `revision`, structure through ProductRevisionView and ProductInstance, DataSet with ExternalFile, UserData/UserValue as the extension mechanism) follow the public schema. **We could not verify from public material** how Teamcenter encodes release status, revision sequence ids, find numbers, change relations and classification (we assume UserData conventions, written down in [plmxml-subset.md](plmxml-subset.md)), nor the exact attribute used to link a ProductRevisionView to its revision. For effectivity: the three effectivity element types and their `start`, `end`, `endCondition` and `contextRef` attributes follow the public schema; that a `ProductInstance` lists its ranges in `effectivityRefs`, the attributes of `LotEffectivity`, whether `end` of a date is inclusive and how Teamcenter fills `contextRef` are our assumptions.

## PTC Windchill REST Services (OData)
* Domains overview (ProdMgmt, DocMgmt, ChangeMgmt and others): https://support.ptc.com/help/windchill_rest_services/r2.4/en/windchill_rest_services/WCCG_RESTAPIsAccessDomainsOverview.html (earlier releases: r1.6, and the Windchill 13.1 help center)
* Product Management domain (parts and BOMs; navigations such as `PartDocAssociations`, documents referenced from DocMgmt; versions 1-5 deprecated from WRS 2.6): https://support.ptc.com/help/windchill/r13.1.0.0/en/Windchill_Help_Center/WCRESTFramework/prodmgmtdomain.html
* Supported OData query options (`$expand`, `$filter`, paging): https://support.ptc.com/help/windchill_rest_services/r2.3/en/windchill_rest_services/WCCG_RESTAPIsSupportedQueryOptions.html

What this did and did not establish: the domains and the navigation from parts to uses and to describing/reference documents. **We did not confirm** the property lists of the entities (including ChangeMgmt change notices, requests and problem reports, whose details the search did not return); the column names in [windchill-extract-spec.md](windchill-extract-spec.md) are our contract, to be reconciled with a real extract.

## Standards used by the importer
ASME Y14.35 revision labels (Keel A7), DoDI 5230.24 distribution statements and the CUI Registry (as in ADR-0004), RFC 4180 (CSV), ISO 8601 (timestamps).
