namespace PoolSense.Api.Options;

public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "ActiveDirectory";
    private const string FscoSupportLevel3 = "FSCO CW SUPPORT LEVEL 3";
    private const string FscoAdmin = "FSCO_Admin";
    private const string FscoCW = "FSCO_CW";
    private const string FscoDeveloper = "FSCO_Developer";
    private const string FscoSupportLevel1 = "FSCO CW SUPPORT LEVEL 1";
    private const string FscoSupportLevel2 = "FSCO CW SUPPORT LEVEL 2";
    private const string ScdsPromiseSupport = "SCDS PROMISE Support";
    private const string ScdsPromiseCW = "SCDS PROMISE CW";

    public string Url { get; set; } = "ldap://corpad.intel.com:3268";

    public string BaseDn { get; set; } = "DC=Corp,DC=intel,DC=com";

    public string Domain { get; set; } = "corp.intel.com";

    public string[] AllowedGroups { get; set; } =
    [
        FscoSupportLevel3,
        FscoAdmin,
        FscoCW,
        FscoDeveloper,
        FscoSupportLevel1,
        FscoSupportLevel2,
        ScdsPromiseSupport,
        ScdsPromiseCW
    ];

    public string[] AdminGroupNames { get; set; } =
    [
        FscoSupportLevel3,
        FscoAdmin
    ];
}