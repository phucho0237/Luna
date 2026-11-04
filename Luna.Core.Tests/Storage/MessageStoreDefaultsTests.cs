using Luna.Core.Storage;

namespace Luna.Core.Tests.Storage;

public class MessageStoreDefaultsTests
{
    [Fact]
    public void DefaultPath_is_history_db_inside_a_Luna_folder_under_local_app_data()
    {
        string path = MessageStoreDefaults.DefaultPath;

        Assert.Equal("history.db", Path.GetFileName(path));
        Assert.Equal("Luna", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.True(Path.IsPathRooted(path));
        // Only the string is checked: the test never creates the folder or the file.
    }
}
