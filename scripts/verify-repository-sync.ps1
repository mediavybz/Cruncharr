param(
    [string]$PrimaryRemote = 'origin',
    [string]$BackupRemote = 'github',
    [switch]$Fetch,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
function Invoke-Git([string[]]$GitArgs) {
    $result = @(& git @GitArgs)
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs[0]) failed" }
    return $result
}
if ($Fetch) {
    $null = Invoke-Git @('fetch', $PrimaryRemote, '--tags')
    $null = Invoke-Git @('fetch', $BackupRemote, '--tags')
}
function Read-RemoteRefs([string]$Remote) {
    @(Invoke-Git @('ls-remote', '--heads', '--tags', $Remote) |
        Where-Object { $_ -notmatch '\^\{\}$' } |
        ForEach-Object { $_ -replace '\s+', ' ' } | Sort-Object)
}
$primary = @(Read-RemoteRefs $PrimaryRemote)
$backup = @(Read-RemoteRefs $BackupRemote)
$local = @(Invoke-Git @('for-each-ref', '--format=%(objectname) %(refname)', 'refs/heads', 'refs/tags') | Sort-Object)
if (!$primary.Count -or !$backup.Count -or !$local.Count) { throw 'A repository returned no refs' }
$differences = @(
    Compare-Object $primary $backup | ForEach-Object { "Remote difference: $($_.InputObject) $($_.SideIndicator)" }
    Compare-Object $primary $local | ForEach-Object { "Local difference: $($_.InputObject) $($_.SideIndicator)" }
)
$changes = @(Invoke-Git @('status', '--porcelain'))
$report = [ordered]@{
    checkedAtUtc = [DateTime]::UtcNow.ToString('o')
    scope = 'Git refs and local working tree only. Check Forgejo hooks and database with verify-forgejo-metadata.sh.'
    synchronized = $differences.Count -eq 0 -and $changes.Count -eq 0
    currentBranch = (Invoke-Git @('branch', '--show-current')) -join ''
    refs = $primary
    differences = $differences
    uncommittedFiles = $changes
    note = 'Compare matching branch names. Stable master and beta testing may have different commits.'
}
$json = $report | ConvertTo-Json -Depth 4
if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false)) }
$json
if (!$report.synchronized) { exit 1 }
