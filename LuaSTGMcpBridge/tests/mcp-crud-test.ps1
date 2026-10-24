#requires -Version 5.1
<#
.SYNOPSIS
    End-to-end test of the LuaSTGMcpBridge CRUD commands against a running editor.

.DESCRIPTION
    Talks to the bridge on 127.0.0.1:8090 (newline delimited JSON) and walks through:
        ping -> get_tree -> insert_node -> get_tree -> set_attribute -> get_tree -> delete_node -> get_tree
    The default flow pauses between steps so you can watch the node tree change in the editor.
    Every mutation goes through a real editor Command, so Undo / Redo must work afterwards.

.PARAMETER Step
    all (default) runs the whole demo, or run one step: ping, tree, insert, set, delete.

.PARAMETER Path
    Node path such as 0/0/1. Required by -Step set and -Step delete.

.PARAMETER Type
    Node type to insert. Default Comment (a general purpose core node).

.PARAMETER Attr / Value
    Attribute name and value used by insert_node and set_attribute.

.EXAMPLE
    .\mcp-crud-test.ps1

.EXAMPLE
    .\mcp-crud-test.ps1 -Step tree

.EXAMPLE
    .\mcp-crud-test.ps1 -Step delete -Path 0/0

.NOTES
    ASCII only on purpose: Windows PowerShell 5.1 reads .ps1 files as ANSI unless they carry a
    BOM, so non-ASCII characters here would need the file to be re-saved as UTF-8 with BOM.
    Attribute values you pass on the command line are not affected by this.
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'ping', 'tree', 'insert', 'set', 'delete')]
    [string]$Step = 'all',

    [int]$Port = 8090,

    [string]$Path,

    [string]$Type = 'Comment',

    [string]$Attr = 'Comment',

    [string]$Value = 'Hello from MCP',

    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# --------------------------------------------------------------------------- bridge client

function Send-Bridge {
    param([Parameter(Mandatory = $true)][hashtable]$Payload)

    $json = $Payload | ConvertTo-Json -Compress -Depth 12
    $client = $null
    $stream = $null
    $writer = $null
    $reader = $null
    $line = $null

    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect('127.0.0.1', $Port)
        $client.NoDelay = $true

        $stream = $client.GetStream()
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($stream, $utf8)
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($stream, $utf8)

        $writer.WriteLine($json)
        $line = $reader.ReadLine()
    }
    catch {
        throw ("Cannot talk to the bridge on 127.0.0.1:{0}. Is the editor running with the MCP bridge started? Original error: {1}" -f $Port, $_.Exception.Message)
    }
    finally {
        if ($reader) { $reader.Dispose() }
        if ($writer) { $writer.Dispose() }
        if ($stream) { $stream.Dispose() }
        if ($client) { $client.Close() }
    }

    if ([string]::IsNullOrEmpty($line)) {
        throw 'The bridge closed the connection without answering.'
    }

    Write-Host ("  -> " + $json) -ForegroundColor DarkGray
    Write-Host ("  <- " + $line) -ForegroundColor DarkGray

    return $line | ConvertFrom-Json
}

function Assert-Ok {
    param($Response, [string]$What)

    if ($Response.status -eq 'ok' -or $Response.status -eq 'pong') {
        Write-Host ("OK   " + $What) -ForegroundColor Green
        return
    }

    Write-Host ("FAIL " + $What) -ForegroundColor Red
    Write-Host ("     status : " + $Response.status) -ForegroundColor Red
    Write-Host ("     error  : " + $Response.error) -ForegroundColor Red

    if ($Response.details) {
        Write-Host ("     details: " + ($Response.details | ConvertTo-Json -Depth 6 -Compress)) -ForegroundColor Red
    }

    if ($Response.status -eq 'no_document') {
        Write-Host '     hint   : open a project or a .lstges file in the editor first.' -ForegroundColor Yellow
    }

    exit 1
}

function Show-Tree {
    param($Node, [int]$Level = 0, [int]$MaxLines = 40)

    if (-not $Node) { return }

    $script:shown = $script:shown + 1
    if ($script:shown -gt $MaxLines) {
        if ($script:shown -eq $MaxLines + 1) {
            Write-Host '  ... (truncated)' -ForegroundColor DarkGray
        }
        return
    }

    $indent = ''.PadLeft($Level * 2)
    $suffix = ''
    if ($Node.banned) { $suffix = '  [banned]' }

    Write-Host ("{0}{1,-10} {2,-14} {3}{4}" -f $indent, $Node.path, $Node.type, $Node.text, $suffix)

    foreach ($child in $Node.children) {
        Show-Tree -Node $child -Level ($Level + 1) -MaxLines $MaxLines
    }
}

function Find-NodeByPath {
    param($Node, [string]$Path)

    if (-not $Node) { return $null }
    if ($Node.path -eq $Path) { return $Node }

    foreach ($child in $Node.children) {
        $found = Find-NodeByPath -Node $child -Path $Path
        if ($null -ne $found) { return $found }
    }

    return $null
}

function Wait-Step {
    param([string]$Message)

    if ($NoPause) { return }
    Write-Host ''
    Read-Host ("[Enter] " + $Message) | Out-Null
}

function Show-Document {
    param($Response)

    Write-Host ('document : ' + $Response.document.name) -ForegroundColor Cyan
    Write-Host ('path     : ' + $Response.document.path) -ForegroundColor Cyan
    Write-Host ('nodes    : ' + $Response.document.nodeCount) -ForegroundColor Cyan
    Write-Host ('unsaved  : ' + $Response.document.dirty) -ForegroundColor Cyan
}

# --------------------------------------------------------------------------- steps

Write-Host ('=== LuaSTG MCP bridge CRUD test, 127.0.0.1:{0} ===' -f $Port) -ForegroundColor Cyan

$ping = Send-Bridge @{ action = 'ping' }

if ($ping.status -ne 'pong') {
    Write-Host 'FAIL the editor did not answer the handshake.' -ForegroundColor Red
    exit 1
}

Write-Host ("OK   connected, editor = " + $ping.editor) -ForegroundColor Green

if ($Step -eq 'ping') {
    Write-Host 'Done.' -ForegroundColor Cyan
    exit 0
}

if ($Step -eq 'tree') {
    $tree = Send-Bridge @{ action = 'get_tree'; path = '0'; maxDepth = 0 }
    Assert-Ok $tree 'get_tree (whole tree)'
    Show-Document $tree

    Write-Host ''
    Write-Host ('{0,-10} {1,-14} {2}' -f 'path', 'type', 'text') -ForegroundColor DarkGray
    $script:shown = 0
    Show-Tree -Node $tree.tree -MaxLines 200

    Write-Host ''
    Write-Host 'Done.' -ForegroundColor Cyan
    exit 0
}

if ($Step -eq 'insert') {
    $insert = Send-Bridge @{
        action     = 'insert_node'
        parent     = '0'
        index      = 0
        type       = $Type
        attributes = @{ $Attr = $Value }
    }
    Assert-Ok $insert ('insert_node ' + $Type)
    Write-Host ('     new path : ' + $insert.path) -ForegroundColor Cyan
    Write-Host ('     text     : ' + $insert.text) -ForegroundColor Cyan
    Write-Host ('     delete it: .\mcp-crud-test.ps1 -Step delete -Path ' + $insert.path) -ForegroundColor Yellow
    exit 0
}

if ($Step -eq 'set') {
    if (-not $Path) {
        Write-Host 'FAIL -Step set needs -Path, for example -Path 0/0' -ForegroundColor Red
        exit 1
    }

    $set = Send-Bridge @{ action = 'set_attribute'; path = $Path; name = $Attr; value = $Value }
    Assert-Ok $set ('set_attribute ' + $Path + ' ' + $Attr)
    Write-Host ('     old : ' + $set.oldValue) -ForegroundColor Cyan
    Write-Host ('     new : ' + $set.newValue) -ForegroundColor Cyan
    Write-Host ('     text: ' + $set.text) -ForegroundColor Cyan
    exit 0
}

if ($Step -eq 'delete') {
    if (-not $Path) {
        Write-Host 'FAIL -Step delete needs -Path, for example -Path 0/0' -ForegroundColor Red
        exit 1
    }

    $delete = Send-Bridge @{ action = 'delete_node'; path = $Path }
    Assert-Ok $delete ('delete_node ' + $Path)
    Write-Host ('     removed : ' + $delete.type + ' "' + $delete.text + '"') -ForegroundColor Cyan
    Write-Host ('     nodes   : ' + $delete.nodeCount) -ForegroundColor Cyan
    exit 0
}

# --------------------------------------------------------------------------- full demo

$before = Send-Bridge @{ action = 'get_tree'; path = '0'; maxDepth = 1 }
Assert-Ok $before 'get_tree (root, depth 1)'
Show-Document $before

Write-Host ''
Write-Host ('{0,-10} {1,-14} {2}' -f 'path', 'type', 'text') -ForegroundColor DarkGray
$script:shown = 0
Show-Tree -Node $before.tree -MaxLines 20

Wait-Step 'look at the node tree in the editor, then press Enter to insert a node'

$insert = Send-Bridge @{
    action     = 'insert_node'
    parent     = '0'
    index      = 0
    type       = $Type
    attributes = @{ $Attr = $Value }
}
Assert-Ok $insert ('insert_node ' + $Type + ' under the root')
$newPath = $insert.path
Write-Host ('     path : ' + $newPath) -ForegroundColor Cyan
Write-Host ('     text : ' + $insert.text) -ForegroundColor Cyan

Wait-Step 'the new row should be selected and visible; press Enter to read it back with get_tree'

$check = Send-Bridge @{ action = 'get_tree'; path = $newPath; maxDepth = 1 }
Assert-Ok $check ('get_tree ' + $newPath)

if ($check.tree.text.IndexOf($Value, [System.StringComparison]::Ordinal) -lt 0) {
    Write-Host ('FAIL text does not contain "' + $Value + '": ' + $check.tree.text) -ForegroundColor Red
    exit 1
}

Write-Host 'OK   the node really is in the tree' -ForegroundColor Green

$newValue = $Value + ' (edited)'
Wait-Step 'press Enter to change that attribute through set_attribute'

$set = Send-Bridge @{ action = 'set_attribute'; path = $newPath; name = $Attr; value = $newValue }
Assert-Ok $set ('set_attribute ' + $Attr)
Write-Host ('     old : ' + $set.oldValue) -ForegroundColor Cyan
Write-Host ('     new : ' + $set.newValue) -ForegroundColor Cyan
Write-Host ('     text: ' + $set.text) -ForegroundColor Cyan

Wait-Step 'the row text in the editor should have changed already; press Enter to verify'

$check2 = Send-Bridge @{ action = 'get_tree'; path = $newPath; maxDepth = 1 }
Assert-Ok $check2 ('get_tree ' + $newPath + ' after edit')

if ($check2.tree.text.IndexOf('edited', [System.StringComparison]::Ordinal) -lt 0) {
    Write-Host ('FAIL text was not updated: ' + $check2.tree.text) -ForegroundColor Red
    exit 1
}

Write-Host 'OK   the attribute change is visible in the tree' -ForegroundColor Green

Wait-Step 'press Enter to delete the node again'

$delete = Send-Bridge @{ action = 'delete_node'; path = $newPath }
Assert-Ok $delete ('delete_node ' + $newPath)
$deletedText = $delete.text
$deletedType = $delete.type
Write-Host ('     removed : ' + $deletedType + ' "' + $deletedText + '"') -ForegroundColor Cyan

Wait-Step 'the row should be gone; press Enter to confirm the old path no longer holds that node'

# Paths are POSITIONAL. Deleting 0/0 makes the former 0/1 move back to 0/0, so a
# successful get_tree on $newPath does NOT mean the deleted node still exists.
# Read the whole tree (without attribute values) and look for the exact path.
$check3 = Send-Bridge @{ action = 'get_tree'; path = '0'; maxDepth = 0; attributes = $false }
Assert-Ok $check3 'get_tree (whole tree after delete)'

$remaining = Find-NodeByPath -Node $check3.tree -Path $newPath

if ($null -ne $remaining -and $remaining.text -eq $deletedText -and $remaining.type -eq $deletedType) {
    Write-Host ('FAIL the deleted node is still in the tree at ' + $newPath) -ForegroundColor Red
    exit 1
}

if ($null -ne $remaining) {
    Write-Host ('OK   ' + $newPath + ' now points at a different node (paths are positional): ' + $remaining.type + ' "' + $remaining.text + '"') -ForegroundColor Green
}
else {
    Write-Host ('OK   no node at ' + $newPath + ' any more (node_not_found, as expected)') -ForegroundColor Green
}

Write-Host ''
Write-Host '=== all commands worked ===' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Now check that Undo / Redo work, which proves every change went through an editor Command:'
Write-Host '  1. click Undo once    -> the deleted node comes back'
Write-Host '  2. click Undo again   -> the attribute reverts to its old value'
Write-Host '  3. click Undo again   -> the inserted node disappears'
Write-Host '  4. click Redo 3 times -> the whole sequence replays'
Write-Host ''
Write-Host 'The tab title should also have picked up a * while the document was dirty.'
Write-Host 'Run ".\mcp-crud-test.ps1 -Step tree" any time to inspect the tree again.'
