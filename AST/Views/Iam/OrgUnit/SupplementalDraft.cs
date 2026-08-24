using System.ComponentModel;
using System.Runtime.CompilerServices;
using AST.Core.Iam.Repositories;

namespace AST.Views.Iam.OrgUnit;

// Dialog-scoped supplemental draft (y = 12 at AdminDivisionLevel 2 / y = 14 at level 3;
// reserves excluded from y per §2.4).
public sealed class SupplementalDraft : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _businessNumber = string.Empty;
    public string BusinessNumber { get => _businessNumber; set => Set(ref _businessNumber, value); }

    private string _nameFullEn = string.Empty;
    public string NameFullEn { get => _nameFullEn; set => Set(ref _nameFullEn, value); }

    private string _nameShortEn = string.Empty;
    public string NameShortEn { get => _nameShortEn; set => Set(ref _nameShortEn, value); }

    private int _adminDivisionLevel = 2;
    public int AdminDivisionLevel
    {
        get => _adminDivisionLevel;
        set
        {
            if (Set(ref _adminDivisionLevel, value))
            {
                OnPropertyChanged(nameof(IsLevel2));
                OnPropertyChanged(nameof(IsLevel3));
                OnPropertyChanged(nameof(DistrictEnabled));
            }
        }
    }

    public bool IsLevel2 => AdminDivisionLevel == 2;

    public bool IsLevel3 => AdminDivisionLevel == 3;

    // Level-3 district inputs are also disabled while the draft is locked after [Lưu].
    public bool DistrictEnabled => AdminDivisionLevel == 3 && !FieldsLocked;

    private bool _fieldsLocked;
    public bool FieldsLocked
    {
        get => _fieldsLocked;
        set
        {
            if (Set(ref _fieldsLocked, value))
                OnPropertyChanged(nameof(DistrictEnabled));
        }
    }

    private string _addrLineVn = string.Empty;
    public string AddrLineVn { get => _addrLineVn; set => Set(ref _addrLineVn, value); }
    private string _addrLineEn = string.Empty;
    public string AddrLineEn { get => _addrLineEn; set => Set(ref _addrLineEn, value); }
    private string _addrWardVn = string.Empty;
    public string AddrWardVn { get => _addrWardVn; set => Set(ref _addrWardVn, value); }
    private string _addrWardEn = string.Empty;
    public string AddrWardEn { get => _addrWardEn; set => Set(ref _addrWardEn, value); }
    private string _addrDistrictVn = string.Empty;
    public string AddrDistrictVn { get => _addrDistrictVn; set => Set(ref _addrDistrictVn, value); }
    private string _addrDistrictEn = string.Empty;
    public string AddrDistrictEn { get => _addrDistrictEn; set => Set(ref _addrDistrictEn, value); }
    private string _addrProvinceVn = string.Empty;
    public string AddrProvinceVn { get => _addrProvinceVn; set => Set(ref _addrProvinceVn, value); }
    private string _addrProvinceEn = string.Empty;
    public string AddrProvinceEn { get => _addrProvinceEn; set => Set(ref _addrProvinceEn, value); }

    private string _phone = string.Empty;
    public string Phone { get => _phone; set => Set(ref _phone, value); }
    private string _fax = string.Empty;
    public string Fax { get => _fax; set => Set(ref _fax, value); }
    private string _email = string.Empty;
    public string Email { get => _email; set => Set(ref _email, value); }

    public (int Filled, int Total) CountProgress()
    {
        var values = new List<string>
        {
            BusinessNumber, NameFullEn, NameShortEn,
            AddrLineVn, AddrLineEn, AddrWardVn, AddrWardEn,
            AddrProvinceVn, AddrProvinceEn,
            Phone, Fax, Email,
        };
        if (AdminDivisionLevel != 2)
        {
            values.Add(AddrDistrictVn);
            values.Add(AddrDistrictEn);
        }

        return (values.Count(v => !string.IsNullOrWhiteSpace(v)), values.Count);
    }

    public SupplementalDraft Clone() => new()
    {
        BusinessNumber = BusinessNumber,
        NameFullEn = NameFullEn,
        NameShortEn = NameShortEn,
        AdminDivisionLevel = AdminDivisionLevel,
        AddrLineVn = AddrLineVn,
        AddrLineEn = AddrLineEn,
        AddrWardVn = AddrWardVn,
        AddrWardEn = AddrWardEn,
        AddrDistrictVn = AddrDistrictVn,
        AddrDistrictEn = AddrDistrictEn,
        AddrProvinceVn = AddrProvinceVn,
        AddrProvinceEn = AddrProvinceEn,
        Phone = Phone,
        Fax = Fax,
        Email = Email,
        FieldsLocked = FieldsLocked,
    };

    public OrgUnitSupplementalDto ToDto() => new(
        BusinessNumber: NullIfBlank(BusinessNumber),
        AddrLineVn: NullIfBlank(AddrLineVn),
        AddrLineEn: NullIfBlank(AddrLineEn),
        AddrWardVn: NullIfBlank(AddrWardVn),
        AddrWardEn: NullIfBlank(AddrWardEn),
        AddrDistrictVn: NullIfBlank(AddrDistrictVn),
        AddrDistrictEn: NullIfBlank(AddrDistrictEn),
        AddrProvinceVn: NullIfBlank(AddrProvinceVn),
        AddrProvinceEn: NullIfBlank(AddrProvinceEn),
        AdminDivisionLevel: (byte)AdminDivisionLevel,
        NameFullEn: NullIfBlank(NameFullEn),
        NameShortEn: NullIfBlank(NameShortEn),
        Phone: NullIfBlank(Phone),
        Fax: NullIfBlank(Fax),
        Email: NullIfBlank(Email));

    public static SupplementalDraft FromDto(OrgUnitSupplementalDto dto) => new()
    {
        BusinessNumber = dto.BusinessNumber ?? string.Empty,
        NameFullEn = dto.NameFullEn ?? string.Empty,
        NameShortEn = dto.NameShortEn ?? string.Empty,
        AdminDivisionLevel = dto.AdminDivisionLevel,
        AddrLineVn = dto.AddrLineVn ?? string.Empty,
        AddrLineEn = dto.AddrLineEn ?? string.Empty,
        AddrWardVn = dto.AddrWardVn ?? string.Empty,
        AddrWardEn = dto.AddrWardEn ?? string.Empty,
        AddrDistrictVn = dto.AddrDistrictVn ?? string.Empty,
        AddrDistrictEn = dto.AddrDistrictEn ?? string.Empty,
        AddrProvinceVn = dto.AddrProvinceVn ?? string.Empty,
        AddrProvinceEn = dto.AddrProvinceEn ?? string.Empty,
        Phone = dto.Phone ?? string.Empty,
        Fax = dto.Fax ?? string.Empty,
        Email = dto.Email ?? string.Empty,
    };

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
