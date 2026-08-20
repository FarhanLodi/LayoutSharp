using System.Security.Cryptography;

namespace LayoutSharp.Tests;

/// <summary>
/// Tiny hand-built ONNX graphs (a few hundred bytes each) that reproduce the three bring-your-own
/// output contracts with constant outputs, so <see cref="LayoutSharp.Models.CustomLayoutModel"/>
/// can be exercised end-to-end — session creation, preprocessing, decoding, error messages —
/// without downloading a real detector.
/// </summary>
/// <remarks>
/// Each graph consumes its image input (<c>ReduceMean</c> multiplied by zero) so ONNX Runtime keeps
/// the input alive, then emits a constant tensor. Generated with <c>onnx.helper</c> (opset 17,
/// IR version 8) and embedded as base64 rather than committed as binary fixtures.
/// </remarks>
internal static class SyntheticOnnxModels
{
    /// <summary>
    /// Ultralytics end-to-end contract: input <c>images [1,3,64,64]</c>, output <c>output0 [1,3,6]</c>
    /// with rows <c>[8,8,40,24,0.9,0]</c>, <c>[16,32,60,60,0.6,1]</c> and a zero padding row.
    /// </summary>
    public const string YoloEndToEndBase64 = "CAgSEWxheW91dHNoYXJwLXRlc3RzOvQCCisKBmltYWdlcxIEbWVhbiIKUmVkdWNlTWVhbioPCghrZWVwZGltcxgAoAECCi4SBHplcm8iCENvbnN0YW50KhwKBXZhbHVlKhAQAUIGemVyb192SgQAAAAAoAEECh4KBG1lYW4KBHplcm8SC3plcm9fc2NhbGFyIgNNdWwKgAESCW91dHB1dDBfYyIIQ29uc3RhbnQqaQoFdmFsdWUqXQgBCAMIBhABQglvdXRwdXQwX3ZKSAAAAEEAAABBAAAgQgAAwEFmZmY/AAAAAAAAgEEAAABCAABwQgAAcEKamRk/AACAPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKABBAomCglvdXRwdXQwX2MKC3plcm9fc2NhbGFyEgdvdXRwdXQwIgNBZGQSCXN5bnRoZXRpY1ogCgZpbWFnZXMSFgoUCAESEAoCCAEKAggDCgIIQAoCCEBiHQoHb3V0cHV0MBISChAIARIMCgIIAQoCCAMKAggGQgQKABAR";

    /// <summary>
    /// Raw Ultralytics head (no NMS): input <c>images [1,3,64,64]</c>, output <c>output0 [1,6,10]</c>
    /// — i.e. <c>[1, 4 + 2 classes, anchors]</c>, which the YoloEndToEnd contract must reject.
    /// </summary>
    public const string YoloRawHeadBase64 = "CAgSEWxheW91dHNoYXJwLXRlc3RzOp8ECisKBmltYWdlcxIEbWVhbiIKUmVkdWNlTWVhbioPCghrZWVwZGltcxgAoAECCi4SBHplcm8iCENvbnN0YW50KhwKBXZhbHVlKhAQAUIGemVyb192SgQAAAAAoAEECh4KBG1lYW4KBHplcm8SC3plcm9fc2NhbGFyIgNNdWwKqwISCW91dHB1dDBfYyIIQ29uc3RhbnQqkwIKBXZhbHVlKoYCCAEIBggKEAFCCW91dHB1dDBfdkrwAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKABBAomCglvdXRwdXQwX2MKC3plcm9fc2NhbGFyEgdvdXRwdXQwIgNBZGQSCXN5bnRoZXRpY1ogCgZpbWFnZXMSFgoUCAESEAoCCAEKAggDCgIIQAoCCEBiHQoHb3V0cHV0MBISChAIARIMCgIIAQoCCAYKAggKQgQKABAR";

    /// <summary>
    /// DETR contract: input <c>pixel_values [1,3,64,64]</c>, outputs <c>logits [1,3,3]</c> (best
    /// class 0, then 2, then a below-threshold query) and <c>pred_boxes [1,3,4]</c>.
    /// </summary>
    public const string DetrBase64 = "CAgSEWxheW91dHNoYXJwLXRlc3RzOpYECjEKDHBpeGVsX3ZhbHVlcxIEbWVhbiIKUmVkdWNlTWVhbioPCghrZWVwZGltcxgAoAECCi4SBHplcm8iCENvbnN0YW50KhwKBXZhbHVlKhAQAUIGemVyb192SgQAAAAAoAEECh4KBG1lYW4KBHplcm8SC3plcm9fc2NhbGFyIgNNdWwKWhIIbG9naXRzX2MiCENvbnN0YW50KkQKBXZhbHVlKjgIAQgDCAMQAUIIbG9naXRzX3ZKJAAAgEAAAADAAABAwAAAQMAAAIC/AABAQAAAoMAAAKDAAACgwKABBAokCghsb2dpdHNfYwoLemVyb19zY2FsYXISBmxvZ2l0cyIDQWRkCm4SDHByZWRfYm94ZXNfYyIIQ29uc3RhbnQqVAoFdmFsdWUqSAgBCAMIBBABQgxwcmVkX2JveGVzX3ZKMAAAgD4AAIA+AAAAPwAAAD8AAEA/AABAPwAAAD8AAAA/AAAAPwAAAD/NzEw+zcxMPqABBAosCgxwcmVkX2JveGVzX2MKC3plcm9fc2NhbGFyEgpwcmVkX2JveGVzIgNBZGQSCXN5bnRoZXRpY1omCgxwaXhlbF92YWx1ZXMSFgoUCAESEAoCCAEKAggDCgIIQAoCCEBiHAoGbG9naXRzEhIKEAgBEgwKAggBCgIIAwoCCANiIAoKcHJlZF9ib3hlcxISChAIARIMCgIIAQoCCAMKAggEQgQKABAR";

    /// <summary>
    /// PaddleDetection contract with V3-style ordered rows: inputs <c>im_shape</c>, <c>image
    /// [1,3,64,64]</c>, <c>scale_factor</c>; outputs <c>fetch_name_0 [3,7]</c>
    /// (class, score, x1, y1, x2, y2, order — ranks 2, 5, 9 in score order 0.9, 0.8, 0.7),
    /// <c>fetch_name_1</c> int32 count and a <c>fetch_name_2 [3,8,8]</c> int32 mask head that must
    /// be ignored (and not even fetched).
    /// </summary>
    public const string PaddleOrderedBase64 = "CAgSEWxheW91dHNoYXJwLXRlc3RzOvMKCioKBWltYWdlEgRtZWFuIgpSZWR1Y2VNZWFuKg8KCGtlZXBkaW1zGACgAQIKLhIEemVybyIIQ29uc3RhbnQqHAoFdmFsdWUqEBABQgZ6ZXJvX3ZKBAAAAACgAQQKHgoEbWVhbgoEemVybxILemVyb19zY2FsYXIiA011bAqEARIGcm93c19jIghDb25zdGFudCpwCgV2YWx1ZSpkCAMIBxABQgZyb3dzX3ZKVAAAgD9mZmY/AAAAQgAAAAAAAIBCAACAQQAAAEAAAAAAzcxMPwAAAAAAAAAAAAAAQgAAgEEAAKBAAAAAQDMzMz8AAAAAAAAAQgAAgEIAAIBCAAAQQaABBAooCgZyb3dzX2MKC3plcm9fc2NhbGFyEgxmZXRjaF9uYW1lXzAiA0FkZAo3EgxmZXRjaF9uYW1lXzEiCENvbnN0YW50Kh0KBXZhbHVlKhEIARAGQgVjbnRfdkoEAwAAAKABBAq7BhIMZmV0Y2hfbmFtZV8yIghDb25zdGFudCqgBgoFdmFsdWUqkwYIAwgICAgQBkIGbWFza192SoAGAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAoAEEEhBzeW50aGV0aWNfcGFkZGxlWhoKCGltX3NoYXBlEg4KDAgBEggKAggBCgIIAlofCgVpbWFnZRIWChQIARIQCgIIAQoCCAMKAghACgIIQFoeCgxzY2FsZV9mYWN0b3ISDgoMCAESCAoCCAEKAggCYh4KDGZldGNoX25hbWVfMBIOCgwIARIICgIIAwoCCAdiGgoMZmV0Y2hfbmFtZV8xEgoKCAgGEgQKAggBYiIKDGZldGNoX25hbWVfMhISChAIBhIMCgIIAwoCCAgKAggIQgQKABAR";

    /// <summary>Writes a base64 graph to a uniquely named file under the test temp directory and returns its path.</summary>
    public static string WriteTo(string base64, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "layoutsharp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        return path;
    }

    /// <summary>Uppercase hex SHA-256 of a file, in the format the registry and specs use.</summary>
    public static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
