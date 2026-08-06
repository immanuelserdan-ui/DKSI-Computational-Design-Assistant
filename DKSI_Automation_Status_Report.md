# DKSI Automation Programme — Executive Status Report

| Field | Detail |
|-------|--------|
| **Programme Start Date** | 15 July 2026 |
| **Report Date** | 6 August 2026 |
| **Reporting Period** | 15 July 2026 – 6 August 2026 |
| **Prepared for** | Senior Management, Engineering Leads, and Programme Stakeholders |
| **Prepared by** | John Immanuel Serdan |
| **Classification** | Internal — Programme Status |

---

## 1. Executive Summary

The DKSI Automation programme has reached a significant inflection point. Eleven workstreams have advanced into the production-ready or testing-ready band (80–100% complete), covering the core drafting and documentation activities that historically consumed the largest share of manual production hours: finish quantification, opening control, schedule generation, and parameter management.

Three structural milestones are worth flagging to leadership in particular:

**Retirement of the Parts and Roombook workflow.** This is the single largest production-time recovery in the programme. Parts and Roombook have historically consumed a disproportionate share of modelling and documentation hours — Parts splitting inflates model size and destabilises element references, while Roombook demands continuous manual reconciliation whenever room or finish data changes. The automated finish-area calculation replaces both, converting a recurring, project-long manual burden into a computed result. See Section 2.1a.

**Elimination of third-party application dependency.** The built-in schedule exporter removes the organisation's reliance on external Excel export applications and bespoke macros. This retires a recurring licence and maintenance exposure and brings the export path under internal version control.

**Retirement of the Dynamo / Dynamo Player wrapper.** All delivered tooling is now consolidated behind a single, company-branded Revit Add-in carrying the corporate identity. This replaces the previous fragmented Dynamo graph-and-player distribution model with a governed, centrally deployed C# codebase — improving reliability, reducing per-user setup, and establishing a single upgrade surface for future releases.

The programme's principal constraint is not internal engineering capacity but **external vision-API token limits and associated cost**. This bottleneck has stalled the image-driven and computer-vision workstreams and, by extension, defers the entry point into the Scan-to-BIM pipeline. A commercial decision on vision-service budget is the gating item for that portfolio.

**Headline position:** Tier 1 drafting automation is substantially delivered and entering validation. Tier 2 (QA/QC) is intentionally queued behind Tier 1 closure. Tier 3 (Scan-to-BIM and machine perception) is defined but not started, pending resolution of the vision-cost constraint.

---

## 2. Production-Ready & Tested Features

The following items are complete or near-complete and are released for deployment or Senior Project Engineer validation.

| # | Capability | Status | Disposition |
|---|-----------|--------|-------------|
| 1 | Finish Calculations — Wall, Ceiling, Floor paint finish areas | 90% | Ready for Senior Project Engineer testing |
| 2 | Interior Wall Sweeps — auto-placement | 80% | Cleared for production release |
| 3 | Door Schedule Logic — "Door From / Door To" mapping | Complete | Ready for Senior Project Engineer testing |
| 4 | Parameter Linking — Text-to-Type to Instance binding | 100% | Ready for Senior Project Engineer testing |
| 5 | Openings Control — lining on/off and material rules | Complete | Ready for Senior Project Engineer testing |
| 6 | Room Binding — room parameters to Wall/Floor/Ceiling schedules | Complete | Ready for Senior Project Engineer testing |
| 7 | Standard Templates — required parameter auto-population | Complete | Ready for Senior Project Engineer testing |
| 8 | Excel Export — built-in schedule exporter | Complete | Ready for Senior Project Engineer testing |
| 9 | Custom Add-in Wrapper — branded centralised Revit Add-in | Complete | Implemented |
| 10 | Room Bounding — automated spatial adjustment | Complete | Ready for Senior Project Engineer testing |
| 11 | Time Management & Monitoring tool | 30% production | In progressive rollout |

### 2.1 Finish Calculations (90%)

Automated derivation of wall, ceiling, and floor paint finish areas. This capability is positioned to **replace the Parts and Roombook workflows** entirely, removing a well-known source of model bloat and manual reconciliation error. The remaining 10% concerns edge-case geometry validation; the module is released to Senior Project Engineers for testing.

### 2.1a Retirement of the Parts and Roombook Workflow — *Production-Time Recovery Milestone*

The Parts and Roombook workflows represent the largest identified consumer of avoidable production time in the current drafting process. Their retirement is the principal business justification for the Finish Calculations workstream and warrants separate visibility at management level.

**Why the legacy workflow is expensive:**

| Cost driver | Effect on production |
|-------------|---------------------|
| **Parts splitting** | Every finish face requires a Part to be created and maintained. Parts multiply element counts, inflate file size, degrade model performance, and break element references on host modification — forcing re-work rather than update. |
| **Manual Roombook maintenance** | Finish data must be re-entered or re-reconciled whenever room boundaries, finishes, or schedules change. The effort recurs on every design revision rather than being incurred once. |
| **Revision fragility** | Because the data is manually maintained, it silently drifts out of alignment with the model. Detecting and correcting that drift is itself a recurring QA cost. |
| **Non-transferable knowledge** | The workflow depends on individual drafters' familiarity with Parts behaviour, creating key-person dependency and a long onboarding curve. |

**What replaces it:** wall, ceiling, and floor paint finish areas are now derived directly from model geometry and bound room parameters (Sections 2.1 and 2.6). Finish quantities become a computed output that refreshes with the model rather than a manually maintained dataset. Parts creation is no longer required for finish quantification, and Roombook is removed from the production chain.

**Strategic significance:**

- **Recurring effort converted to one-time setup.** The saving is realised on every revision cycle of every project, not once per project.
- **Model health improvement.** Removing Parts proliferation reduces file size and improves model stability and performance — a benefit extending beyond the finish workflow.
- **Reduced revision risk.** Computed finish data cannot drift from the model, removing a standing source of documentation error.
- **Lower onboarding cost.** New staff no longer need to master Parts and Roombook conventions to produce compliant finish documentation.

**Action required:** the time saving is currently qualitative. It is recommended that a **baseline measurement be captured on the first pilot project** — manual Parts/Roombook hours per project phase versus automated hours — so that the return can be stated numerically in the next reporting cycle. The Time Management & Monitoring tool (Section 2.11) is the natural instrument for this measurement, which strengthens the case for completing it.

**Dependency note:** decommissioning cannot be declared complete until Finish Calculations passes Senior Project Engineer testing (remaining 10%). Until then, the legacy workflow should be treated as *superseded but not yet withdrawn*, and teams should not remove fallback capability mid-project.

### 2.2 Interior Wall Sweeps (80%)

Automated placement of interior wall sweeps. Cleared for production release with the understood caveat that **minor manual adjustment remains necessary** in non-standard conditions. The residual manual effort is materially smaller than full manual placement and is considered an acceptable production trade-off at this stage.

### 2.3 Door Schedule Logic

The "Door From / Door To" parameter mapping now resolves dynamically in schedules, replacing static orientation codes. This eliminates a persistent manual-editing and error-correction task in door documentation.

### 2.4 Parameter Linking (100%)

Text-to-Type to Instance parameter binding is fully complete and aligned to the current standard template. Development on this workstream is closed; it is released for Senior Project Engineer testing alongside the other Tier 1 items.

### 2.5 Openings Control

Rules-based automation governs door and window lining visibility (on/off) and material application. This encodes previously tacit drafting conventions into deterministic logic.

### 2.6 Room Binding

Room parameters are programmatically bound to Wall, Floor, and Ceiling schedules via C#. This is the connective tissue that makes the finish and quantity schedules self-populating rather than manually maintained.

### 2.7 Standard Templates

Auto-population of required parameters for DKSI's new standard template is implemented, ensuring new projects start compliant rather than being remediated later.

### 2.8 Excel Export — *Dependency Elimination Milestone*

A native schedule exporter is built into the add-in. **Third-party export applications and Excel macros are no longer required.** This removes external licence exposure, eliminates macro-security friction, and places the export format under internal change control.

### 2.9 Custom Add-in Wrapper — *Platform Consolidation Milestone*

A centralised, company-branded Revit Add-in bearing the corporate logo now hosts the toolset. **Dynamo and Dynamo Player are no longer dependencies.** Benefits: single deployment artefact, consistent user experience, controlled versioning, and no reliance on end users managing graph files.

### 2.10 Room Bounding

Automated adjustment scripts resolve complex spatial logic across ceiling, floor, and roof interactions — historically one of the more error-prone and time-consuming manual coordination tasks.

### 2.11 Time Management & Monitoring (30% production)

The DKSI Automated Time Management & Monitoring tool is at 30% production status. It is the least mature item in this section and should be read as *in progressive rollout* rather than delivered. Recommend a defined completion target be set alongside Tier 1 closure.

---

## 3. Ongoing Scripting & Development Pipeline

| Item | Status | Constraint |
|------|--------|-----------|
| Stair Void Automation | Not started | Capacity / sequencing |
| Auto-Generated Walls from Images | 20% built — **on hold** | External vision API token limits and cost |
| Computer Vision Identification | Not started — deferred | Token / vision overhead |
| Danish-to-English Document Translator | Planned | Scope definition pending |

### 3.1 Stair Void Automation
Not yet commenced. Scoped and held in the backlog pending Tier 1 closure and engineering capacity.

### 3.2 Auto-Generated Walls from Images — *Primary Bottleneck*
Development reached approximately 20% before being suspended. The blocker is **external**: vision-API token limits and the associated per-call cost make the current architecture commercially unviable at production volume. This is not an engineering capability gap.

**Decision required from leadership:** either (a) approve a vision-service budget envelope sufficient for production throughput, (b) authorise investigation of a locally hosted or self-managed vision model to remove per-token cost, or (c) formally defer the workstream. Until one of these is chosen, the item remains parked and consumes no engineering capacity.

### 3.3 Computer Vision Identification
Deferred for the same reason as 3.2. It shares the token and vision-overhead constraint and should be treated as a dependent item — it becomes actionable only once the vision-cost decision is made.

### 3.4 Danish-to-English Document Translator
Planned for technical document workflows. Lower technical risk than the vision items and not subject to the same constraint; a candidate for opportunistic scheduling if the vision decision is delayed.

---

## 4. Strategic Roadmap & Future Projects

### 4.1 Scan-to-BIM Pipeline — *Not Started*

The most ambitious element of the forward roadmap, comprising three sequential components:

1. **Automated Point Cloud Semantic Segmentation & Classification** — machine classification of raw scan data into building element categories.
2. **Algorithmic Point-to-Revit API Bridge** — programmatic generation of families and walls directly from classified point data.
3. **Open-BIM (IFC) Automated Compliance & Deviation Checker** — automated validation of delivered models against IFC compliance rules and as-built deviation tolerances.

**Assessment:** These components are strictly sequential — segmentation feeds the API bridge, which in turn produces the models the compliance checker validates. The pipeline also inherits the machine-perception cost profile identified in Section 3, and should be understood as **downstream of the same vision/compute investment decision**. Its economics deserve a dedicated business case before commencement rather than being absorbed into general development capacity.

### 4.2 QA/QC Tier Alignment

QA/QC add-ins are **deliberately queued** until all Tier 1 drafting add-ins are fully finalised. This is a sound sequencing decision: QA/QC logic must validate against a stable, settled set of drafting rules. Building checkers against a moving target would generate rework and false failures.

**Implication for planning:** the QA/QC tier's start date is a direct function of Tier 1 closure. The critical path to QA/QC therefore runs through the remaining 10% of Finish Calculations, the 20% of Interior Wall Sweeps, and the completion target for the Time Management tool.

### 4.3 Recommended Tier Structure

| Tier | Scope | Status |
|------|-------|--------|
| **Tier 1** | Drafting & documentation automation | Substantially delivered; entering validation |
| **Tier 2** | QA/QC add-ins | Gated on Tier 1 closure |
| **Tier 3** | Scan-to-BIM & machine perception | Not started; gated on vision-cost decision |

---

## 5. Risks & Actions

| # | Risk / Item | Impact | Recommended Action | Owner |
|---|------------|--------|-------------------|-------|
| 1 | External vision-API token limits and cost | Blocks two workstreams; defers Tier 3 | Leadership decision on budget vs. self-hosted model vs. formal deferral | Management |
| 2 | Tier 1 residual completion (Finish Calcs 10%, Sweeps 20%) | Gates the entire QA/QC tier | Set firm closure date; prioritise Senior Project Engineer testing capacity | Engineering Lead |
| 3 | Time Management tool at 30% | Reported alongside completed items; maturity mismatch | Define completion target and rollout plan | Development |
| 4 | Interior Wall Sweeps require manual adjustment | Residual manual effort in production | Document the known edge cases for drafting staff | Development |
| 5 | Scan-to-BIM scope undefined commercially | Risk of open-ended investment | Commission a business case before any development start | Management |
| 6 | Parts/Roombook time saving is unquantified | Return on the largest milestone cannot be stated numerically | Capture baseline hours on first pilot project | Engineering Lead |
| 7 | Premature withdrawal of Parts/Roombook fallback | Live projects left without a working finish workflow | Withdraw only after Finish Calculations testing closes | Engineering Lead |

---

## 6. Recommended Next Steps

1. **Allocate Senior Project Engineer testing capacity** to Finish Calculations — this is the highest-value validation currently outstanding and sits on the critical path to Tier 1 closure.
2. **Take the vision-service decision.** Two workstreams and the entire Tier 3 roadmap are blocked behind a single commercial question. Resolving it either unblocks meaningful development or cleanly removes it from the plan.
3. **Set a formal Tier 1 closure date**, defined by completion of Finish Calculations, Interior Wall Sweeps, and the Time Management tool. This date is effectively the QA/QC tier start date.
4. **Nominate a pilot project for Parts/Roombook decommissioning** and capture baseline hours against it, so the programme's largest time saving can be reported as a figure rather than a claim.
5. **Publish the deployment note** for the consolidated add-in, confirming to all users that Dynamo Player and third-party Excel export tools can be retired from their workflows — the benefit of these milestones is only realised once the old paths are actually stood down.
6. **Schedule the Danish-to-English translator** as constraint-free work that can progress independently of the vision decision.

---

## 7. Note on Revision

This report reflects programme status as at the date of issue and is issued as a working document.

> **"This report is open for revision and improvement. Percentages, priorities, and sequencing reflect the current development position and are expected to change as testing feedback arrives and as commercial decisions are taken. Corrections, challenges, and additions from engineering leads and stakeholders are actively invited — accuracy of this record matters more than its stability."**

Stakeholders are asked to direct amendments, disputed figures, or additional workstreams to the Computational Design / Automation Development function for incorporation into the next issue.

---

*End of report.*
