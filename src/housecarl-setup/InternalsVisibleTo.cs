using System.Runtime.CompilerServices;

// The installer tests drive the real installer (Program.TryInstall, Uninstall.TryUninstall) and assert on where
// it put things. Without this they would have to rebuild the destination paths themselves, and a test holding
// its own copy of the paths would follow the installer wherever it went. They call the path helpers instead.
[assembly: InternalsVisibleTo("housecarl-mcp-tests")]
