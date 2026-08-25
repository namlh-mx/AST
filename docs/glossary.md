# Bilingual Glossary (VN ↔ EN) — terminology anchor for project AST

> Purpose: keep the Vietnamese terms (docs/business) and the English terms (code/identifiers) from drifting apart in meaning.
> Convention: code/identifiers use the EN column; Vietnamese documents use the VN column, with the EN term in parentheses when needed.
> When a new term appears → add it here.

## Effective period (temporal)
| VN | EN (used in code/identifiers) | Note |
|---|---|---|
| kỳ hiệu lực | effective period | `[F,T]` = `effective_from`..`effective_to` |
| kỳ mở / chưa xác định | open period | `effective_to = 9999-12-31`, UI: "Not yet determined" |
| ngày giao dịch (D) | transaction/business date | source: the business-date-provider abstraction, `AST.Core/Time/` |
| thẻ căn cước (id bền) | identity / header (durable id) | table `<name>` |
| phiên bản | version | table `<name>_version` |
| đóng băng (giá trị đã dùng) | freeze / snapshot | a transaction stores the version id |
| đại số khoảng kỳ (8 ca) | interval algebra (8-case algebra) | the period-editing engine, `AST.Core/EffectivePeriod/`, §4 of the doc |
| remnant (mảnh còn lại khi cắt kỳ) | remnant | a new version that keeps the old data, only the period changes |
| cảnh báo khoảng trống | gap warning | a date gap upon declaration |
| toàn vẹn tham chiếu theo thời gian | temporal foreign key (temporal-FK) | STRICT level; the temporal-FK validator, `AST.Core/EffectivePeriod/` |
| phân giải theo ngày | resolution / as-of | the period resolver, `AST.Core/EffectivePeriod/` |
| xóa mềm | soft delete | `isactive = 0` |
| đang hiệu lực (còn công nhận) | active | `isactive = 1` |
| đóng băng (kỳ) | freeze (a period) | see the "freeze / snapshot" row above; also used for a superseded period kept for audit |
| Cách X/Y | Option X/Y | e.g. Option 1 (middleware tier) vs. Option 2 (direct DB connection) — `docs/design-iam-foundation.md §5` |
| hạng tham số (theo thời gian) | temporality class | Current vs Declared — `docs/design-temporality-classes.md` |
| hạng Hiện tại | Current (class) | one table, `isactive`, no period columns; cannot be a temporal-FK parent |
| hạng Khai báo | Declared (class) | header + version, the full effective-period model |
| không hồi tố | `NoBackdate` | orthogonal flag: a declaration may start today or later, never earlier; the DEFAULT for a Declared entity |
| lệnh (yêu cầu thực hiện vào lúc nào) | command | outside the parameter model — e.g. `app_control` |
| sổ ghi (bản ghi sự kiện, chỉ thêm) | ledger | outside the parameter model — e.g. `audit_log`, and processed transactions |

## IAM / organization
| VN | EN | Note |
|---|---|---|
| đơn vị | org unit | table `org_unit`, parent-child tree |
| mã đơn vị | org code | `org_unit_version.org_code`, business code / natural key (P6); app: 4-8 chars, letters+digits, ALL CAPS |
| tên đầy đủ (đơn vị) | full name (VN) | `org_unit_version.org_name_full_vn`, legal profile name |
| tên viết tắt (đơn vị) | short name (VN) | `org_unit_version.org_name_short_vn`, internal-management name |
| thông tin bổ sung (đơn vị) | supplemental fields | optional org-unit columns (`org_business_number`, address, EN names, phone/fax/email, reserves) — catalog in declaration-screens spec §2.4 |
| bị hủy (kế hoạch tương lai) | cancelled (plan) | `org_unit_version.status = 'cancelled'` + `isactive = 0` (was a `cancelled` column until V010): a future version closed before it took effect (distinct from a naturally-ended/superseded version) |
| bị thay thế | replaced | `org_unit_version.status = 'replaced'` + `isactive = 0` + a non-null `replaced_by_org_unit_id`: a version retired by a replacement gesture, told apart from a naturally-ended one only by that durable marker. Org-unit only in v1 — `chk_rv_status`/`chk_rpv_status` do not admit the value at all |
| vai trò | role | `role` |
| mã vai trò | role code | `role_version.role_code`, business code / natural key (P6) |
| tên vai trò | role name | `role_version.role_name` |
| vai trò quản trị (cờ) | admin role (flag) | `role_version.is_admin_role`, version-level flag; N-14 #2: ≤1 active admin-flag role per day, enforced by `IntegrityCheckService` sweep |
| người dùng | user | `user`; key = `username` (samAccountName) |
| chức năng | function | `function`, `function_key` = `Module.Entity.Action` |
| mã nghiệp vụ | business code | e.g. `FX002`, for display |
| phân quyền | permission / role permission | `role_permission` |
| phạm vi dữ liệu | data scope | the data-scope value, `AST.Core/Iam/`, 4 levels |
| bản thân / đúng đơn vị / đơn vị + con / toàn hệ thống | Self / OwnOrgUnit / OwnOrgUnitAndDescendants / Global | the scope-level enum (1..4), `AST.Core/Iam/` = `role_permission_version.scope_level` |
| admin gốc (break-glass) | break-glass admin | a list of usernames in a signed config file |
| nhóm menu | menu group | the menu-group code constants, `AST.Core/Iam/` |
| đồng bộ danh mục chức năng | function catalog sync | the function-catalog sync service, `AST.Core/Iam/`; auto ADD/EDIT only, never auto-REMOVE (`docs/design-function-catalog-sync.md`) |
| chức năng nghi đã gỡ | removal candidate | present in the DB (active), absent from code — awaiting admin confirmation before closing |
| chức năng nghi khôi phục | reopen candidate | present in code, has an old closed identity — awaiting admin to reopen (re-add, reusing the identity) |

## Foundation / technical
| VN | EN | Note |
|---|---|---|
| Tầng nền | SharedKernel | project `AST.Core` |
| ranh giới module | module boundary | skill `rule-module-boundary` |
| kho dữ liệu nền (ép 3 điều kiện) | base repository | the standard scope-filter builder, `AST.Core/Data/` |
| tiêm phụ thuộc | dependency injection (DI) | Prism.DryIoc |
| khóa chống ghi đồng thời | named lock | MySQL `GET_LOCK` |
| chữ ký số | digital signature | self-generated RSA/ECDSA key pair, a `.sig` file |
| Lát | slice | a phase/slice label used in project tracking (e.g. Slice #2) |
| Đn (mốc quyết định) | Dn | decision-log anchor, e.g. D1..D13, D13a, D13b |
| người yêu cầu | requester | the project's non-technical business stakeholder; source of truth for business decisions |

## Operations / deployment (2026-07-03 addendum — see `docs/archive/2026-07-03-addendum-proposals.md`)
| VN | EN | Note |
|---|---|---|
| phiên bản schema | schema version | table `schema_version`, checked by the app at startup |
| script migration đánh số | numbered migration script | `migrations/V00X__...sql`, run manually via DBeaver |
| thăm dò định kỳ | polling | the polling service, `AST.Infrastructure/`, replaces "push realtime" |
| nhật ký kiểm toán | audit log | table `audit_log`, append-only in the DB |
| log kỹ thuật | technical/diagnostic log | Serilog, local file `%LOCALAPPDATA%\AST\logs\` |
| thư mục theo phiên bản | versioned folder deployment | `\\share\AST\v1.2.3\` + launcher |
| lệnh điều khiển app (hẹn giờ đóng) | app control command | table `app_control`, the app polls + auto-closes |
| kiểm tra toàn vẹn dữ liệu | data integrity check | admin screen, detects overlaps/gapped coverage |
| lỗi tạm thời (kết nối) | transient error | retry + backoff, a shared policy in `AST.Core` |
| rà soát nợ kỹ thuật | tech-debt review | a periodic session at milestones, producing a refactor task list |
