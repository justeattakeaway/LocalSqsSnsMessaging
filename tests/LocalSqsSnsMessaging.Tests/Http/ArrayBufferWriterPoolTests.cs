using System.Buffers;
using System.Text;
using LocalSqsSnsMessaging.Http;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Http;

/// <summary>
/// Tests for ArrayBufferWriter pooling to verify:
/// - Buffers are reused
/// - Buffers are cleared before reuse
/// - Large buffers are discarded
/// - No memory leaks
/// </summary>
public class ArrayBufferWriterPoolTests
{
    [Test]
    public void Rent_ShouldReturnBuffer()
    {
        // Act
        var buffer = ArrayBufferWriterPool.Rent();

        // Assert
        buffer.ShouldNotBeNull();
        buffer.Capacity.ShouldBeGreaterThan(0);
    }

    [Test]
    public void RentAndReturn_ShouldReuseBuffer()
    {
        // Arrange - rent and return a buffer
        var buffer1 = ArrayBufferWriterPool.Rent();
        ArrayBufferWriterPool.Return(buffer1);

        // Act - rent again
        var buffer2 = ArrayBufferWriterPool.Rent();

        // Assert - should be the same instance (per-thread caching)
        buffer2.ShouldBeSameAs(buffer1);
    }

    [Test]
    public void Return_ShouldClearBuffer()
    {
        // Arrange - write some data
        var buffer = ArrayBufferWriterPool.Rent();
        var span = buffer.GetSpan(10);
        "Hello"u8.CopyTo(span);
        buffer.Advance(5);
        buffer.WrittenCount.ShouldBe(5);

        // Act - return and rent again
        ArrayBufferWriterPool.Return(buffer);
        var rentedAgain = ArrayBufferWriterPool.Rent();

        // Assert - should be cleared
        rentedAgain.WrittenCount.ShouldBe(0);
    }

    [Test]
    public void PooledArrayBufferWriter_ShouldAutoReturn()
    {
        ArrayBufferWriter<byte>? capturedBuffer = null;

        // Arrange & Act - use RAII wrapper
        using (var pooled = PooledArrayBufferWriter.Rent())
        {
            capturedBuffer = pooled.Writer;
            pooled.Writer.ShouldNotBeNull();

            // Write some data
            var span = pooled.Writer.GetSpan(10);
            "Test"u8.CopyTo(span);
            pooled.Writer.Advance(4);
        }

        // Assert - buffer should be returned and reusable
        var nextBuffer = ArrayBufferWriterPool.Rent();
        nextBuffer.ShouldBeSameAs(capturedBuffer);
        nextBuffer.WrittenCount.ShouldBe(0); // Should be cleared
    }

    [Test]
    public void MultipleRent_WithoutReturn_ShouldCreateNewBuffers()
    {
        // Arrange & Act - rent multiple without returning
        var buffer1 = ArrayBufferWriterPool.Rent();
        var buffer2 = ArrayBufferWriterPool.Rent();
        var buffer3 = ArrayBufferWriterPool.Rent();

        // Assert - should all be different instances
        buffer1.ShouldNotBeSameAs(buffer2);
        buffer2.ShouldNotBeSameAs(buffer3);
        buffer1.ShouldNotBeSameAs(buffer3);
    }

    [Test]
    public void Return_WithNullBuffer_ShouldNotThrow()
    {
        // Act & Assert
        Should.NotThrow(() => ArrayBufferWriterPool.Return(null!));
    }

    [Test]
    public void Return_LargeBuffer_ShouldNotCache()
    {
        // Arrange - create a large buffer
        var largeBuffer = new ArrayBufferWriter<byte>(2 * 1024 * 1024); // 2MB

        // Write to make it grow
        var span = largeBuffer.GetSpan(2 * 1024 * 1024);
        largeBuffer.Advance(2 * 1024 * 1024);

        // Act - return the large buffer
        ArrayBufferWriterPool.Return(largeBuffer);

        // Rent a new one
        var nextBuffer = ArrayBufferWriterPool.Rent();

        // Assert - should be a different buffer (large one was discarded)
        nextBuffer.ShouldNotBeSameAs(largeBuffer);
        nextBuffer.Capacity.ShouldBeLessThan(largeBuffer.Capacity);
    }

    [Test]
    public void PooledArrayBufferWriter_MultipleIterations_ShouldReuse()
    {
        ArrayBufferWriter<byte>? firstBuffer = null;

        // First iteration
        using (var pooled = PooledArrayBufferWriter.Rent())
        {
            firstBuffer = pooled.Writer;
            var span = pooled.Writer.GetSpan(100);
            "First"u8.CopyTo(span);
            pooled.Writer.Advance(5);
        }

        // Second iteration - should reuse the same buffer
        using (var pooled = PooledArrayBufferWriter.Rent())
        {
            pooled.Writer.ShouldBeSameAs(firstBuffer);
            pooled.Writer.WrittenCount.ShouldBe(0); // Should be cleared
        }
    }

    [Test]
    public void RealUsagePattern_SerializeMultipleTimes_ShouldReuseBuffer()
    {
        // Simulate real usage: serialize multiple responses
        var results = new List<byte[]>();

        for (int i = 0; i < 5; i++)
        {
            using var pooled = PooledArrayBufferWriter.Rent();

            // Simulate writing data
            var data = Encoding.UTF8.GetBytes($"Response {i}");
            var span = pooled.Writer.GetSpan(data.Length);
            data.CopyTo(span);
            pooled.Writer.Advance(data.Length);

            results.Add(pooled.Writer.WrittenMemory.ToArray());
        }

        // Assert - all responses should be different
        results.Count.ShouldBe(5);
        for (int i = 0; i < 5; i++)
        {
            var expected = Encoding.UTF8.GetBytes($"Response {i}");
            results[i].ShouldBe(expected);
        }
    }

    [Test]
    public void PooledArrayBufferWriter_ExceptionDuringUse_ShouldStillReturn()
    {
        ArrayBufferWriter<byte>? capturedBuffer = null;

        // Act - exception during use
        try
        {
            using var pooled = PooledArrayBufferWriter.Rent();
            capturedBuffer = pooled.Writer;
            throw new InvalidOperationException("Test exception");
        }
        catch (InvalidOperationException)
        {
            // Expected
        }

        // Assert - buffer should still be returned to pool
        var nextBuffer = ArrayBufferWriterPool.Rent();
        nextBuffer.ShouldBeSameAs(capturedBuffer);
    }
}
