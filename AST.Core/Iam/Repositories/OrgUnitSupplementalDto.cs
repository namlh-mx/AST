namespace AST.Core.Iam.Repositories;

// Optional §2.4 catalog (address/contact/EN-name profile). All fields nullable except
// AdminDivisionLevel, which mirrors the DDL default (2) so "no supplemental supplied" still
// reads as a valid value, not an absent one.
public sealed record OrgUnitSupplementalDto(
    string? BusinessNumber = null,
    string? AddrLineVn = null,
    string? AddrLineEn = null,
    string? AddrWardVn = null,
    string? AddrWardEn = null,
    string? AddrDistrictVn = null,
    string? AddrDistrictEn = null,
    string? AddrProvinceVn = null,
    string? AddrProvinceEn = null,
    byte AdminDivisionLevel = 2,
    string? NameFullEn = null,
    string? NameShortEn = null,
    string? Phone = null,
    string? Fax = null,
    string? Email = null);
