namespace PoolSense.Api.Options;

public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "ActiveDirectory";
    private const string FscoSupportLevel3 = "FSCO CW SUPPORT LEVEL 3";
    private const string FscoAdmin = "FSCO_Admin";

    public string Url { get; set; } = "ldap://corpad.intel.com:3268";

    public string BaseDn { get; set; } = "DC=Corp,DC=intel,DC=com";

    public string Domain { get; set; } = "corp.intel.com";

    public string[] AllowedGroups { get; set; } = [];

    public string[] AdminGroupNames { get; set; } =
    [
        FscoSupportLevel3,
        FscoAdmin
    ];
}