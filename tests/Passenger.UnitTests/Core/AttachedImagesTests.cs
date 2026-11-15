using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>macOS's disk images as hdiutil lists them attached: each image by its file as given, its whole disk, and where
/// its volumes are mounted, one still being attached passed over; what holds a disk as lsof lists it; a disk's device
/// nodes told from another disk's; a tool's words without hdiutil's deprecation warnings, and whether they refuse
/// access.</summary>
public class AttachedImagesTests
{
    /// <summary>As hdiutil info -plist lists three images: one mounted, one whose volume was unmounted while its disk stayed
    /// attached, and one still being attached, which has no disk yet.</summary>
    private const string Listed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>framework</key>
            <string>704</string>
            <key>images</key>
            <array>
                <dict>
                    <key>diskimages2</key>
                    <true/>
                    <key>hdid-pid</key>
                    <integer>18454</integer>
                    <key>image-path</key>
                    <string>/tmp/updates/1.0.0/Passenger-macos-arm64.dmg</string>
                    <key>system-entities</key>
                    <array>
                        <dict>
                            <key>content-hint</key>
                            <string>GUID_partition_scheme</string>
                            <key>dev-entry</key>
                            <string>/dev/disk10</string>
                        </dict>
                        <dict>
                            <key>content-hint</key>
                            <string>48465300-0000-11AA-AA11-00306543ECAC</string>
                            <key>dev-entry</key>
                            <string>/dev/disk10s1</string>
                            <key>mount-point</key>
                            <string>/private/tmp/updates/1.0.0/mount</string>
                        </dict>
                    </array>
                    <key>writeable</key>
                    <false/>
                </dict>
                <dict>
                    <key>image-path</key>
                    <string>/Users/someone/Tom &amp; Jerry.dmg</string>
                    <key>system-entities</key>
                    <array>
                        <dict>
                            <key>dev-entry</key>
                            <string>/dev/disk9s1</string>
                        </dict>
                        <dict>
                            <key>dev-entry</key>
                            <string>/dev/disk9</string>
                        </dict>
                    </array>
                </dict>
                <dict>
                    <key>diskimages2</key>
                    <true/>
                    <key>image-path</key>
                    <string>/tmp/updates/1.0.0/Passenger-macos-arm64.dmg</string>
                    <key>image-type</key>
                    <string>read-only disk image</string>
                </dict>
            </array>
        </dict>
        </plist>
        """;

    [Fact]
    public void Each_attached_image_is_its_file_as_given_its_whole_disk_and_where_its_volumes_are_mounted()
    {
        IReadOnlyList<AttachedImages.Attachment> attached = AttachedImages.Read(Listed);
        Assert.Equal(2, attached.Count);
        Assert.Equal("/tmp/updates/1.0.0/Passenger-macos-arm64.dmg", attached[0].Image);
        Assert.Equal("/dev/disk10", attached[0].Disk);
        Assert.Equal(["/private/tmp/updates/1.0.0/mount"], attached[0].Mounts);
        Assert.Equal("/Users/someone/Tom & Jerry.dmg", attached[1].Image);
        Assert.Equal("/dev/disk9", attached[1].Disk);
        Assert.Empty(attached[1].Mounts);
    }

    [Fact]
    public void A_mac_with_no_image_attached_lists_none()
    {
        Assert.Empty(AttachedImages.Read("""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict><key>framework</key><string>704</string><key>images</key><array/></dict></plist>
            """));
    }

    [Fact]
    public void What_holds_a_disk_is_each_process_lsof_lists_by_name_and_id_with_what_it_has_open()
    {
        string fields = "p16356\ncperl\nf3\nn/dev/rdisk5\np200\ncmds_stores\nf4\nn/dev/disk5s1\nf5\nn/dev/disk5s1\nf6\nn/dev/rdisk5s1\n";
        Assert.Equal(["perl (process 16356) on /dev/rdisk5", "mds_stores (process 200) on /dev/disk5s1, /dev/rdisk5s1"], AttachedImages.HoldersOf(fields));
        Assert.Empty(AttachedImages.HoldersOf(string.Empty));
        Assert.Equal("nothing this user runs had it open", AttachedImages.Held([]));
        Assert.Equal("held by perl (process 1) on /dev/rdisk5", AttachedImages.Held(["perl (process 1) on /dev/rdisk5"]));
    }

    [Theory]
    [InlineData("disk5", true)]
    [InlineData("rdisk5", true)]
    [InlineData("disk5s1", true)]
    [InlineData("rdisk5s12", true)]
    [InlineData("disk5s1s2", true)]
    [InlineData("disk50", false)]
    [InlineData("disk50s1", false)]
    [InlineData("rdisk4", false)]
    [InlineData("disk5s", false)]
    public void A_disks_device_nodes_are_it_its_slices_and_both_raw_never_another_disks(string entry, bool ofDisk5)
    {
        Assert.Equal(ofDisk5, AttachedImages.NodeOf(entry, "disk5"));
    }

    [Fact]
    public void A_tools_words_leave_out_hdiutils_deprecation_warnings_and_are_its_exit_code_where_nothing_is_left()
    {
        ChildResult busy = new(16, string.Empty,
            "hdiutil: WARNING: 'hdiutil detach ...' is deprecated. Please use 'diskutil eject ...' instead.\nhdiutil: couldn't eject \"disk5\" - Resource busy\n");
        Assert.Equal("hdiutil: couldn't eject \"disk5\" - Resource busy", AttachedImages.Words(busy));
        Assert.Equal("Volume failed to eject", AttachedImages.Words(new ChildResult(1, string.Empty, "Volume failed to eject\n")));
        ChildResult silent = new(1, string.Empty, "hdiutil: WARNING: 'hdiutil detach -force ...' is deprecated.\n");
        Assert.Equal(ExitCodes.Said(1), AttachedImages.Words(silent));
    }

    [Theory]
    [InlineData("hdiutil: attach failed - Permission denied", true)]
    [InlineData("diskutil: The operation couldn\u2019t be completed. Permission denied", true)]
    [InlineData("diskutil: The operation couldn\u2019t be completed. Operation not permitted.", true)]
    [InlineData("hdiutil: attach failed - Resource temporarily unavailable", false)]
    [InlineData("hdiutil: attach failed - image not recognized", false)]
    [InlineData("diskutil: The disk image is corrupted", false)]
    public void A_tools_words_refuse_access_where_they_end_saying_permission_was_denied_or_not_permitted(string said, bool refused)
    {
        Assert.Equal(refused, AttachedImages.RefusesAccess(said));
    }
}
