using System;

namespace BmwCoder.Transport;

/// <summary>
/// Exception thrown when an error occurs during HSFZ framing or transport communication.
/// </summary>
public class HsfzException : Exception
{
    public HsfzException(string message) : base(message)
    {
    }

    public HsfzException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
