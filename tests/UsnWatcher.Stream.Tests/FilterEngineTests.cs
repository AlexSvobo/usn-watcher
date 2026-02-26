using System;
using UsnWatcher.Core;
using UsnWatcher.Stream;
using Xunit;

namespace UsnWatcher.Stream.Tests
{
    public class FilterEngineTests
    {
        private static UsnRecord Make(string fileName, string? fullPath = null, string[]? reasons = null, bool isDir = false)
        {
            return new UsnRecord
            {
                Usn = 1,
                Timestamp = DateTime.UtcNow,
                FileReferenceNumber = 1,
                ParentFileReferenceNumber = 2,
                FileName = fileName,
                FullPath = fullPath,
                Reasons = reasons ?? Array.Empty<string>(),
                ReasonRaw = 0,
                IsDirectory = isDir,
                FileAttributes = 0
            };
        }

        [Fact]
        public void NoFilter_AllowsEverything()
        {
            var f = new FilterEngine(null);
            var r = Make("Program.cs", "C:\\proj\\Program.cs", new[] { "CLOSE" });
            Assert.True(f.Matches(r));
        }

        [Fact]
        public void ExtPredicate_MatchesExtension()
        {
            var f = new FilterEngine("ext:.cs");
            Assert.True(f.Matches(Make("a.cs")));
            Assert.False(f.Matches(Make("a.txt")));
        }

        [Fact]
        public void PathPredicate_MatchesFullPathOrName()
        {
            var f = new FilterEngine("path:proj");
            Assert.True(f.Matches(Make("Program.cs", "C:\\proj\\Program.cs")));
            Assert.True(f.Matches(Make("myprojfile.txt")));
            Assert.False(f.Matches(Make("other.txt", "C:\\other\\file.txt")));
        }

        [Fact]
        public void ReasonPredicate_MatchesReason()
        {
            var f = new FilterEngine("reason:CLOSE");
            Assert.True(f.Matches(Make("a.txt", reasons: new[] { "CLOSE" })));
            Assert.False(f.Matches(Make("a.txt", reasons: new[] { "DATA_OVERWRITE" })));
        }

        [Fact]
        public void NamePredicate_MatchesFileNameSubstring()
        {
            var f = new FilterEngine("name:temp");
            Assert.True(f.Matches(Make("tempfile.txt")));
            Assert.False(f.Matches(Make("file.txt")));
        }

        [Fact]
        public void DirPredicate_MatchesDirectoryFlag()
        {
            var yes = new FilterEngine("dir:true");
            var no = new FilterEngine("dir:false");
            Assert.True(yes.Matches(Make("folder", isDir: true)));
            Assert.False(yes.Matches(Make("file.txt", isDir: false)));
            Assert.True(no.Matches(Make("file.txt", isDir: false)));
        }

        [Fact]
        public void AndOrNot_PrecedenceAndWorks()
        {
            var f = new FilterEngine("ext:.cs OR name:program AND NOT dir:true");
            // ext match should pass
            Assert.True(f.Matches(Make("a.cs", isDir: false)));
            // name+not dir should pass
            Assert.True(f.Matches(Make("program.txt", isDir: false)));
            // name with dir true should be excluded
            Assert.False(f.Matches(Make("program.txt", isDir: true)));
            // unrelated should be false
            Assert.False(f.Matches(Make("other.txt", isDir: false)));
        }
    }
}
