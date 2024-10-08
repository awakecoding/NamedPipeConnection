# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License.

@{

# Script module or binary module file associated with this manifest.
RootModule = '.\Microsoft.PowerShell.NamedPipeConnection.dll'

# Version number of this module.
ModuleVersion = '1.0.0'

# Supported PSEditions
CompatiblePSEditions = @('Core')

# ID used to uniquely identify this module
GUID = '39e7aaeb-e6da-4f3b-af00-59f6ee3747c5'

# Author of this module
Author = 'Microsoft Corporation'

# Company or vendor of this module
CompanyName = 'Microsoft Corporation'

# Copyright statement for this module
Copyright = '(c) Microsoft Corporation. All rights reserved.'

# Description of the functionality provided by this module
Description = "

This PowerShell module implements a custom remoting connection based on named pipes.
It is primarily an example of how to use PowerShell APIs to create custom remote connections.
But it is also used in PowerShell CI to test the custom remoting APIs.
"

# Minimum version of the PowerShell engine required by this module
PowerShellVersion = '7.3.0'
DotNetFrameworkVersion = '7.0'
CLRVersion = '4.0.0'

# Cmdlets to export from this module, for best performance, do not use wildcards and do not delete the entry, use an empty array if there are no cmdlets to export.
CmdletsToExport = @('New-PSHostSession')

FunctionsToExport = @()

# Private data to pass to the module specified in RootModule/ModuleToProcess. This may also contain a PSData hashtable with additional module metadata used by PowerShell.
PrivateData = @{

    PSData = @{

        # Tags applied to this module. These help with module discovery in online galleries.
        Tags = @('PowerShellCustomRemoteConnection')

        # A URL to the license for this module.
        LicenseUri = 'https://github.com/awakecoding/NamedPipeConnection/blob/master/LICENSE'

        # A URL to the main website for this project.
        ProjectUri = 'https://github.com/awakecoding/NamedPipeConnection'

        # A URL to an icon representing this module.
        # IconUri = ''

        # ReleaseNotes of this module
        # ReleaseNotes = ''

        # Prerelease string of this module
        # Prerelease = 'beta'

        # Flag to indicate whether the module requires explicit user acceptance for install/update/save
        # RequireLicenseAcceptance = $false

        # External dependent modules of this module
        # ExternalModuleDependencies = @()
    } # End of PSData hashtable

} # End of PrivateData hashtable

# HelpInfo URI of this module
HelpInfoURI = 'https://aka.ms/ps-modules-help'

}
