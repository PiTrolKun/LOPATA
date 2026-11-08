param([Parameter(Mandatory=$true)][string]$InputPdf,
      [Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
# Destination sizes are DIPs; explicitly account for the system's raster scale
# so a high-DPI desktop cannot multiply our requested pixel/memory budget.
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class ReaderDpi { [DllImport("user32.dll")] public static extern uint GetDpiForSystem(); [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value); }'
$null=[ReaderDpi]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
$rasterScale=[Math]::Max(1,[ReaderDpi]::GetDpiForSystem()/96.0)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null=[Windows.Data.Pdf.PdfDocument,Windows.Data.Pdf,ContentType=WindowsRuntime]
$null=[Windows.Data.Pdf.PdfPageRenderOptions,Windows.Data.Pdf,ContentType=WindowsRuntime]
$null=[Windows.Storage.Streams.IRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
$generic=[System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object {$_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'}|Select-Object -First 1
$action=[System.WindowsRuntimeSystemExtensions].GetMethods()|Where-Object {$_.Name -eq 'AsTask' -and -not $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'}|Select-Object -First 1
function Wait-Result($operation,[type]$type){$task=$generic.MakeGenericMethod($type).Invoke($null,@($operation));$task.GetAwaiter().GetResult()}
function Reply($value){[Console]::WriteLine(($value|ConvertTo-Json -Depth 5 -Compress))}
try {
    $file=Wait-Result ([Windows.Storage.StorageFile]::GetFileFromPathAsync($InputPdf)) ([Windows.Storage.StorageFile])
    $pdf=Wait-Result ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
    $sizes=@(for($i=0;$i -lt $pdf.PageCount;$i++){$p=$pdf.GetPage([uint32]$i);try{@{width=$p.Size.Width;height=$p.Size.Height}}finally{$p.Dispose()}})
    Reply @{pages=$sizes}
    while($null -ne ($line=[Console]::ReadLine())) {
        try {
            $request=$line|ConvertFrom-Json
            $index=[int]$request.page
            if($index -lt 0 -or $index -ge $pdf.PageCount){throw 'Page index out of range'}
            $width=[Math]::Max(64,[Math]::Min(4096,[int]$request.width))
            $page=$pdf.GetPage([uint32]$index)
            try {
                $output=Join-Path $WorkDirectory ('page-'+$index+'-'+$width+'.png')
                [IO.File]::WriteAllBytes($output,[byte[]]@())
                $outFile=Wait-Result ([Windows.Storage.StorageFile]::GetFileFromPathAsync($output)) ([Windows.Storage.StorageFile])
                $stream=Wait-Result ($outFile.OpenAsync([Windows.Storage.FileAccessMode]::ReadWrite)) ([Windows.Storage.Streams.IRandomAccessStream])
                try {
                    $options=New-Object Windows.Data.Pdf.PdfPageRenderOptions
                    $scale=[Math]::Min($width/$page.Size.Width,4096/$page.Size.Height)
                    $options.DestinationWidth=[uint32][Math]::Max(1,[Math]::Floor($page.Size.Width*$scale/$rasterScale))
                    $options.DestinationHeight=[uint32][Math]::Max(1,[Math]::Floor($page.Size.Height*$scale/$rasterScale))
                    $task=$action.Invoke($null,@($page.RenderToStreamAsync($stream,$options)));$null=$task.GetAwaiter().GetResult()
                    $null=Wait-Result ($stream.FlushAsync()) ([bool])
                } finally {$stream.Dispose()}
                Reply @{file=$output}
            } finally {$page.Dispose()}
        } catch {Reply @{error=$_.Exception.Message}}
    }
} catch {Reply @{error=$_.Exception.Message};exit 1}
