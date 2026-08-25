using System;
using Xunit;

namespace MsmqRestBridge.Tests
{
    public class WorkerLogicTests
    {
        [Theory]
        [InlineData(null, 0)]
        [InlineData(new byte[0], 0)]
        [InlineData(new byte[] { 1, 2 }, 0)]            // too short (< 4 bytes) -> 0
        [InlineData(new byte[] { 3, 0, 0, 0 }, 3)]     // little-endian int 3
        [InlineData(new byte[] { 255, 255, 255, 255 }, 0)] // -1 -> clamped to 0
        public void ReadAttemptCount_HandlesAllCases(byte[] extension, int expected)
        {
            Assert.Equal(expected, Worker.ReadAttemptCount(extension));
        }

        [Fact]
        public void ReadAttemptCount_RoundTripsThroughBitConverter()
        {
            byte[] ext = BitConverter.GetBytes(4);
            Assert.Equal(4, Worker.ReadAttemptCount(ext));
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
