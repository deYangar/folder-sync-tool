$sh = New-Object -ComObject Shell.Application
$rb = $sh.Namespace(10)
$found = $rb.Items() | Where-Object { $_.Name -eq '___recycle_probe.txt' }
if ($found) {
    foreach ($f in $found) {
        Write-Output ("RECYCLE_BIN_CONFIRM: " + $f.Name + " | 原路径: " + $rb.GetDetailsOf($f, 1))
    }
} else {
    Write-Output "RECYCLE_BIN_MISS: 未找到探针文件"
}
