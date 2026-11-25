function ConvertTo-ProcessArgumentLine {
    # Quote each argument for the Windows native command line, preserving empty
    # arguments, embedded quotes and backslashes before a closing quote.
    param([string[]]$Arguments)

    $quoted = foreach ($arg in @($Arguments)) {
        if ($null -eq $arg) {
            '""'
            continue
        }

        $value = [string]$arg
        if ($value.Length -gt 0 -and $value -notmatch '[\s"]') {
            $value
            continue
        }

        $builder = New-Object System.Text.StringBuilder
        [void]$builder.Append('"')
        $backslashes = 0
        foreach ($ch in $value.ToCharArray()) {
            if ($ch -eq '\') {
                $backslashes++
                continue
            }

            if ($ch -eq '"') {
                if ($backslashes -gt 0) {
                    [void]$builder.Append('\' * ($backslashes * 2))
                }
                [void]$builder.Append('\"')
                $backslashes = 0
                continue
            }

            if ($backslashes -gt 0) {
                [void]$builder.Append('\' * $backslashes)
                $backslashes = 0
            }
            [void]$builder.Append($ch)
        }

        if ($backslashes -gt 0) {
            [void]$builder.Append('\' * ($backslashes * 2))
        }
        [void]$builder.Append('"')
        $builder.ToString()
    }

    return ($quoted -join ' ')
}
