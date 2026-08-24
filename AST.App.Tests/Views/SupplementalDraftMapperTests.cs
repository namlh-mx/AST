using AST.Core.Iam.Repositories;
using AST.Views.Iam.OrgUnit;

namespace AST.App.Tests.Views;

// RED (pre-implementation) coverage for SupplementalDraft.ToDto()/FromDto() -- design spec
// the supplemental-save design notes §2.2. Both members
// do not exist yet; this file is expected to fail to COMPILE until they are added. Plain POCO,
// no WPF visual involved, so no Sta.Run needed (matches VersionStatusConverterTests' reasoning).
public class SupplementalDraftMapperTests
{
    private static SupplementalDraft FullyPopulatedDraft() => new()
    {
        BusinessNumber = "0123456789",
        NameFullEn = "Full English Name",
        NameShortEn = "Short EN",
        AdminDivisionLevel = 3,
        AddrLineVn = "Số 1 Đường ABC",
        AddrLineEn = "1 ABC Street",
        AddrWardVn = "Phường 1",
        AddrWardEn = "Ward 1",
        AddrDistrictVn = "Quận 1",
        AddrDistrictEn = "District 1",
        AddrProvinceVn = "TP.HCM",
        AddrProvinceEn = "HCMC",
        Phone = "0281234567",
        Fax = "0287654321",
        Email = "org@example.com",
        FieldsLocked = true,
    };

    [Fact]
    public void ToDto_maps_every_field_of_a_fully_populated_draft()
    {
        var draft = FullyPopulatedDraft();

        var dto = draft.ToDto();

        Assert.Equal(draft.BusinessNumber, dto.BusinessNumber);
        Assert.Equal(draft.NameFullEn, dto.NameFullEn);
        Assert.Equal(draft.NameShortEn, dto.NameShortEn);
        Assert.Equal((byte)3, dto.AdminDivisionLevel);
        Assert.Equal(draft.AddrLineVn, dto.AddrLineVn);
        Assert.Equal(draft.AddrLineEn, dto.AddrLineEn);
        Assert.Equal(draft.AddrWardVn, dto.AddrWardVn);
        Assert.Equal(draft.AddrWardEn, dto.AddrWardEn);
        Assert.Equal(draft.AddrDistrictVn, dto.AddrDistrictVn);
        Assert.Equal(draft.AddrDistrictEn, dto.AddrDistrictEn);
        Assert.Equal(draft.AddrProvinceVn, dto.AddrProvinceVn);
        Assert.Equal(draft.AddrProvinceEn, dto.AddrProvinceEn);
        Assert.Equal(draft.Phone, dto.Phone);
        Assert.Equal(draft.Fax, dto.Fax);
        Assert.Equal(draft.Email, dto.Email);
    }

    [Fact]
    public void ToDto_normalizes_blank_or_whitespace_only_fields_to_null()
    {
        var draft = FullyPopulatedDraft();
        draft.AddrLineVn = "   ";
        draft.Email = "";
        draft.Fax = "\t";

        var dto = draft.ToDto();

        Assert.Null(dto.AddrLineVn);
        Assert.Null(dto.Email);
        Assert.Null(dto.Fax);
        // Untouched fields on the same draft still map through unaffected.
        Assert.Equal(draft.BusinessNumber, dto.BusinessNumber);
        Assert.Equal(draft.AddrLineEn, dto.AddrLineEn);
    }

    [Fact]
    public void ToDto_trims_leading_and_trailing_whitespace_on_non_blank_fields()
    {
        var draft = FullyPopulatedDraft();
        draft.BusinessNumber = "  0123456789  ";

        var dto = draft.ToDto();

        Assert.Equal("0123456789", dto.BusinessNumber);
    }

    [Fact]
    public void FromDto_round_trip_maps_null_strings_to_empty_and_never_sets_FieldsLocked()
    {
        var dto = new OrgUnitSupplementalDto(
            BusinessNumber: "0123456789",
            AddrLineVn: null,
            AddrLineEn: "1 ABC Street",
            AddrWardVn: null,
            AddrWardEn: "Ward 1",
            AddrDistrictVn: null,
            AddrDistrictEn: null,
            AddrProvinceVn: "TP.HCM",
            AddrProvinceEn: null,
            AdminDivisionLevel: 2,
            NameFullEn: null,
            NameShortEn: "Short EN",
            Phone: null,
            Fax: null,
            Email: "org@example.com");

        var draft = SupplementalDraft.FromDto(dto);

        Assert.Equal(string.Empty, draft.AddrLineVn);
        Assert.Equal(string.Empty, draft.AddrWardVn);
        Assert.Equal(string.Empty, draft.AddrDistrictVn);
        Assert.Equal(string.Empty, draft.AddrDistrictEn);
        Assert.Equal(string.Empty, draft.AddrProvinceEn);
        Assert.Equal(string.Empty, draft.NameFullEn);
        Assert.Equal(string.Empty, draft.Phone);
        Assert.Equal(string.Empty, draft.Fax);

        Assert.Equal(dto.BusinessNumber, draft.BusinessNumber);
        Assert.Equal(dto.AddrLineEn, draft.AddrLineEn);
        Assert.Equal(dto.AddrWardEn, draft.AddrWardEn);
        Assert.Equal(dto.AddrProvinceVn, draft.AddrProvinceVn);
        Assert.Equal(dto.NameShortEn, draft.NameShortEn);
        Assert.Equal(dto.Email, draft.Email);
        Assert.Equal(2, draft.AdminDivisionLevel);

        // Dialog-open-state chrome is caller-decided, not part of the persisted shape.
        Assert.False(draft.FieldsLocked);
    }

    [Fact]
    public void ToDto_then_FromDto_round_trip_preserves_every_business_field_of_a_fully_populated_draft()
    {
        var original = FullyPopulatedDraft();

        var roundTripped = SupplementalDraft.FromDto(original.ToDto());

        Assert.Equal(original.BusinessNumber, roundTripped.BusinessNumber);
        Assert.Equal(original.NameFullEn, roundTripped.NameFullEn);
        Assert.Equal(original.NameShortEn, roundTripped.NameShortEn);
        Assert.Equal(original.AdminDivisionLevel, roundTripped.AdminDivisionLevel);
        Assert.Equal(original.AddrLineVn, roundTripped.AddrLineVn);
        Assert.Equal(original.AddrLineEn, roundTripped.AddrLineEn);
        Assert.Equal(original.AddrWardVn, roundTripped.AddrWardVn);
        Assert.Equal(original.AddrWardEn, roundTripped.AddrWardEn);
        Assert.Equal(original.AddrDistrictVn, roundTripped.AddrDistrictVn);
        Assert.Equal(original.AddrDistrictEn, roundTripped.AddrDistrictEn);
        Assert.Equal(original.AddrProvinceVn, roundTripped.AddrProvinceVn);
        Assert.Equal(original.AddrProvinceEn, roundTripped.AddrProvinceEn);
        Assert.Equal(original.Phone, roundTripped.Phone);
        Assert.Equal(original.Fax, roundTripped.Fax);
        Assert.Equal(original.Email, roundTripped.Email);

        // FieldsLocked is deliberately excluded -- ToDto/FromDto never touch it.
    }
}
