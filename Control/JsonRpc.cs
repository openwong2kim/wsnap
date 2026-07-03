// wsnap — macOS-style screen capture for Windows.
// Copyright (C) 2026 openwong2kim and wsnap contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License version 3, as published
// by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License
// for more details. You should have received a copy of the GNU General
// Public License along with this program. If not, see
// <https://www.gnu.org/licenses/>.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Wsnap.Control;

/// <summary>
/// One parsed JSON-RPC 2.0 message. <see cref="Id"/> is preserved verbatim as a
/// <see cref="JsonElement"/> (number | string | null) so a response can echo it byte-for-byte;
/// it is <c>null</c> only when the request carried no <c>id</c> member at all — i.e. a
/// notification, which per spec receives no response. A present-but-null id (<c>"id":null</c>)
/// round-trips as a <see cref="JsonValueKind.Null"/> element and is still a request.
/// </summary>
public readonly struct JsonRpcRequest
{
    /// <summary>The request id, cloned and detached from its source document. Null ⇒ notification.</summary>
    public JsonElement? Id { get; init; }

    /// <summary>The method name, or null when the <c>method</c> member is missing/non-string.</summary>
    public string? Method { get; init; }

    /// <summary>The <c>params</c> value (any JSON), cloned and detached. Null when absent.</summary>
    public JsonElement? Params { get; init; }

    /// <summary>True when there is no id member — a JSON-RPC notification (send no response).</summary>
    public bool IsNotification => Id is null;
}

/// <summary>
/// Minimal, allocation-light JSON-RPC 2.0 helper built on <see cref="System.Text.Json"/> only.
/// Parses newline-delimited request objects and serializes success/error envelopes. Response
/// bodies are written with a hand-rolled recursive value writer (no reflection) so the wire
/// output is deterministic and trim-safe — critical because the MCP stdio transport treats
/// stdout as a pure protocol channel.
/// </summary>
public static class JsonRpc
{
    // ---- Standard JSON-RPC 2.0 error codes. ----
    public const int ParseError     = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams  = -32602;
    public const int InternalError  = -32603;

    /// <summary>
    /// Parse a single message. Returns <c>false</c> only when <paramref name="message"/> is not
    /// valid JSON (caller answers with <see cref="ParseError"/>, id null). A syntactically valid
    /// object with a missing/blank method still returns <c>true</c> with
    /// <see cref="JsonRpcRequest.Method"/> null so the caller can reply
    /// <see cref="InvalidRequest"/> while echoing whatever id was present.
    /// </summary>
    public static bool TryParse(string message, out JsonRpcRequest request)
    {
        request = default;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(message); }
        catch (JsonException) { return false; }

        using (doc)
        {
            var root = doc.RootElement;
            // A non-object root (number, array, string, …) is a malformed request; surface it as a
            // request with no method so the loop replies InvalidRequest (id unavailable ⇒ null).
            if (root.ValueKind != JsonValueKind.Object) { request = default; return true; }

            JsonElement? id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : null;
            string? method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            JsonElement? prms = root.TryGetProperty("params", out var p) ? p.Clone() : null;

            request = new JsonRpcRequest { Id = id, Method = method, Params = prms };
            return true;
        }
    }

    /// <summary>Serialize a <c>{jsonrpc,id,result}</c> success envelope (no trailing newline).</summary>
    public static string Success(JsonElement? id, object? result) =>
        Envelope(id, w => { w.WritePropertyName("result"); WriteValue(w, result); });

    /// <summary>Serialize a <c>{jsonrpc,id,error:{code,message,data?}}</c> error envelope.</summary>
    public static string Error(JsonElement? id, int code, string message, object? data = null) =>
        Envelope(id, w =>
        {
            w.WritePropertyName("error");
            w.WriteStartObject();
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            if (data is not null) { w.WritePropertyName("data"); WriteValue(w, data); }
            w.WriteEndObject();
        });

    /// <summary>Serialize an arbitrary plain object graph to a compact JSON string.</summary>
    public static string Serialize(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) WriteValue(w, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Envelope(JsonElement? id, Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            if (id is { } el) el.WriteTo(w); else w.WriteNullValue();
            writeBody(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Recursively write a plain object graph — null / bool / number / string / <see cref="JsonElement"/>
    /// / string-keyed dictionary / enumerable — with no reflection. Payloads are built from
    /// <see cref="Dictionary{TKey,TValue}"/>, lists and primitives, keeping stdout fully deterministic.
    /// </summary>
    public static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:            w.WriteNullValue(); break;
            case bool b:          w.WriteBooleanValue(b); break;
            case string s:        w.WriteStringValue(s); break; // before IEnumerable (string is char-seq)
            case int i:           w.WriteNumberValue(i); break;
            case long l:          w.WriteNumberValue(l); break;
            case double d:        w.WriteNumberValue(d); break;
            case float f:         w.WriteNumberValue(f); break;
            case JsonElement je:  je.WriteTo(w); break;

            case IReadOnlyDictionary<string, object?> map: // before IEnumerable (dictionary is a KVP-seq)
                w.WriteStartObject();
                foreach (var kv in map) { w.WritePropertyName(kv.Key); WriteValue(w, kv.Value); }
                w.WriteEndObject();
                break;

            case System.Collections.IEnumerable seq:
                w.WriteStartArray();
                foreach (var item in seq) WriteValue(w, item);
                w.WriteEndArray();
                break;

            default:
                w.WriteStringValue(value.ToString());
                break;
        }
    }
}
