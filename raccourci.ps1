# Cree (ou remet a jour) le raccourci "Quadri-Size" sur le bureau.
# Appele par build.bat ; on peut aussi le lancer seul : clic droit > Executer avec PowerShell.
$racine = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $racine 'bin\QuadriSize.exe'
$icone = Join-Path $racine 'assets\quadri-size.ico'

if (-not (Test-Path $exe)) {
    Write-Host "bin\QuadriSize.exe introuvable : lance d'abord build.bat."
    exit 1
}

# GetFolderPath('Desktop') suit le vrai bureau, meme s'il est deplace (OneDrive par exemple).
$bureau = [Environment]::GetFolderPath('Desktop')
$lien = Join-Path $bureau 'Quadri-Size.lnk'

$shell = New-Object -ComObject WScript.Shell
$raccourci = $shell.CreateShortcut($lien)
$raccourci.TargetPath = $exe
$raccourci.WorkingDirectory = Join-Path $racine 'bin'
$raccourci.IconLocation = $icone
$raccourci.Description = 'Quadri-Size : analyseur d''espace disque'
$raccourci.Save()

Write-Host "Raccourci cree : $lien"
