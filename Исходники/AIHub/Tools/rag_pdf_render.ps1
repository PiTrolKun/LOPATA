param([Parameter(Mandatory=$true)][string]$InputPdf,
      [Parameter(Mandatory=$true)][int]$Page,
      [Parameter(Mandatory=$true)][string]$OutputPng)
$ErrorActionPreference = 'Stop'
# Windows PowerShell supplies the .NET Framework WinRT projection; no SDK/runtime download.
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType=WindowsRuntime]
$null = [Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
$generic = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
$actionMethod = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and -not $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
} | Select-Object -First 1
function Wait-Result($operation, [type]$resultType) {
    $task = $generic.MakeGenericMethod($resultType).Invoke($null, @($operation))
    $task.GetAwaiter().GetResult()
}
$file = Wait-Result ([Windows.Storage.StorageFile]::GetFileFromPathAsync($InputPdf)) ([Windows.Storage.StorageFile])
$pdf = Wait-Result ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
$pdfPage = $pdf.GetPage([uint32]($Page - 1))
try {
    [System.IO.File]::WriteAllBytes($OutputPng, [byte[]]@())
    $outFile = Wait-Result ([Windows.Storage.StorageFile]::GetFileFromPathAsync($OutputPng)) ([Windows.Storage.StorageFile])
    $stream = Wait-Result ($outFile.OpenAsync([Windows.Storage.FileAccessMode]::ReadWrite)) ([Windows.Storage.Streams.IRandomAccessStream])
    try {
        $options = New-Object Windows.Data.Pdf.PdfPageRenderOptions
        $scale = [Math]::Min(3, 4096 / [Math]::Max($pdfPage.Size.Width, $pdfPage.Size.Height))
        $options.DestinationWidth = [uint32][Math]::Max(1, [Math]::Ceiling($pdfPage.Size.Width * $scale))
        $options.DestinationHeight = [uint32][Math]::Max(1, [Math]::Ceiling($pdfPage.Size.Height * $scale))
        $action = $pdfPage.RenderToStreamAsync($stream, $options)
        $actionTask = $actionMethod.Invoke($null, @($action))
        $actionTask.GetAwaiter().GetResult()
        $null = Wait-Result ($stream.FlushAsync()) ([bool])
    } finally { $stream.Dispose() }
} finally { $pdfPage.Dispose() }
