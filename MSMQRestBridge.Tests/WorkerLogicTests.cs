using System;
using Xunit;

namespace MsmqRestBridge.Tests
{
    public class WorkerLogicTests
    {
        [Theory]
        [InlineData(null, 0)]
        [InlineData(new byte[0], 0)]
        [InlineData(new byte[] { 1, 2 }, 0)]                                   // too short -> 0
        [InlineData(new byte[] { 3, 0, 0, 0 }, 0)]                             // no marker -> 0 (not our data)
        [InlineData(new byte[] { 77, 82, 66, 1, 3, 0, 0, 0 }, 3)]              // marker + little-endian int 3
        [InlineData(new byte[] { 77, 82, 66, 1, 255, 255, 255, 255 }, 0)]      // marker + -1 -> clamped to 0
        public void ReadAttemptCount_HandlesAllCases(byte[] extension, int expected)
        {
            Assert.Equal(expected, Worker.ReadAttemptCount(extension));
        }

        [Fact]
        public void ReadAttemptCount_RoundTripsThroughBuildRetryExtension()
        {
            byte[] ext = Worker.BuildRetryExtension(4, null);
            Assert.Equal(4, Worker.ReadAttemptCount(ext));
        }

        [Fact]
        public void GetOriginalExtension_PreservesNonMarkerDataAsOriginal()
        {
            byte[] sourceExtension = { 9, 8, 7, 6, 5 };
            Assert.Equal(sourceExtension, Worker.GetOriginalExtension(sourceExtension));
        }

        [Fact]
        public void BuildRetryExtension_PreservesOriginalExtensionAcrossRetries()
        {
            byte[] sourceExtension = { 9, 8, 7, 6, 5 };

            byte[] firstRetryExtension = Worker.BuildRetryExtension(1, Worker.GetOriginalExtension(sourceExtension));
            Assert.Equal(1, Worker.ReadAttemptCount(firstRetryExtension));
            Assert.Equal(sourceExtension, Worker.GetOriginalExtension(firstRetryExtension));

            byte[] secondRetryExtension = Worker.BuildRetryExtension(2, Worker.GetOriginalExtension(firstRetryExtension));
            Assert.Equal(2, Worker.ReadAttemptCount(secondRetryExtension));
            Assert.Equal(sourceExtension, Worker.GetOriginalExtension(secondRetryExtension));
        }

        [Theory]
        [InlineData("short", 10, "short")]
        [InlineData("0123456789extra", 10, "0123456789...")]  // truncated with ellipsis
        [InlineData("", 10, "")]
        [InlineData(null, 10, null)]
        public void Truncate_RespectsMaxLength(string input, int maxLength, string expected)
        {
            Assert.Equal(expected, Worker.Truncate(input, maxLength));
        }

        [Fact]
        public void Truncate_ExactlyAtLimit_NotTruncated()
        {
            string exactly10 = "1234567890";
            Assert.Equal(exactly10, Worker.Truncate(exactly10, 10));
        }

        [Fact]
        public void Truncate_OneOverLimit_AddsSuffix()
        {
            string result = Worker.Truncate("12345678901", 10);
            Assert.Equal("1234567890...", result);
        }
    }
}
