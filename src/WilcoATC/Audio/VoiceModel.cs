namespace WilcoATC.Audio;

/// <summary>
/// Une voix Piper installée : un dossier contenant le modèle VITS (<c>*.onnx</c>),
/// <c>tokens.txt</c> et le dossier <c>espeak-ng-data/</c> (format sherpa-onnx).
/// </summary>
public sealed class VoiceModel
{
    public required string Name { get; init; }       // = nom du dossier (ex. "vits-piper-en_US-ryan-medium")
    public required string OnnxPath { get; init; }    // chemin du modèle .onnx
    public required string TokensPath { get; init; }  // tokens.txt
    public required string DataDir { get; init; }     // dossier espeak-ng-data
}
