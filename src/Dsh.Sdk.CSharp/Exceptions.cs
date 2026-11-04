using System.Text.Json;

namespace DeepSeek.Harness.Sdk;

/// <summary>Base exception for client-side Harness SDK failures.</summary>
public class DeepSeekHarnessException : Exception
{
    public DeepSeekHarnessException(string message) : base(message) { }
    public DeepSeekHarnessException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The Harness runtime returned a JSON-RPC error response.</summary>
public sealed class HarnessRpcException : DeepSeekHarnessException
{
    public HarnessRpcException(int code, string message, JsonElement? data = null)
        : base($"Harness JSON-RPC error {code}: {message}")
    {
        Code = code;
        RpcMessage = message;
        ErrorData = data;
    }

    public int Code { get; }
    public string RpcMessage { get; }
    public JsonElement? ErrorData { get; }
}

/// <summary>The runtime exited or its stdio transport became unusable.</summary>
public sealed class HarnessTransportException : DeepSeekHarnessException
{
    public HarnessTransportException(string message) : base(message) { }
    public HarnessTransportException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>The runtime sent a response outside the SDK wire protocol.</summary>
public sealed class HarnessProtocolException : DeepSeekHarnessException
{
    public HarnessProtocolException(string message) : base(message) { }
    public HarnessProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A JSON-RPC request exceeded the configured timeout.</summary>
public sealed class HarnessRequestTimeoutException : DeepSeekHarnessException
{
    public HarnessRequestTimeoutException(string method, TimeSpan timeout)
        : base($"Harness request '{method}' exceeded its timeout of {timeout}.")
    {
        Method = method;
        Timeout = timeout;
    }

    public string Method { get; }
    public TimeSpan Timeout { get; }
}
