# Currently, using a fixed list of most downloaded packages as of 02/25/2015. Consider changing this logic to always pull the latest packages and operate on that

$topDownloadedPackageIds = (
    'Newtonsoft.Json', # Lib files
    'jQuery', # content files, PS scripts (install.ps1 and uninstall.ps1)
    'EntityFramework', # Web.config transforms, PS scripts (init.ps1 and install.ps1)
    'Microsoft.AspNet.Mvc', # Deep dependency graph
    'Microsoft.Net.Http', # Framework references, PCL Target framework folders
    'WindowsAzure.Storage', # Reference elements in nuspec. And, deep dependency graph
    'Antlr', # No target framework specific folders
    'Microsoft.Bcl.Build', # .Targets files
    'angularjs'<#,
    'Microsoft.AspNet.WebPages',
    'Microsoft.AspNet.Razor',
    'Microsoft.AspNet.WebApi.Client',
    'Microsoft.AspNet.WebApi.Core',
    'Microsoft.AspNet.WebApi.WebHost',
    'Microsoft.AspNet.WebApi',
    'Microsoft.AspNet.Web.Optimization',
    'WebGrease',
    'jQuery.Validation',
    'jQuery.UI.Combined',
    'Microsoft.jQuery.Unobtrusive.Validation',
    'Microsoft.Data.Edm',
    'Microsoft.Data.OData',
    'System.Spatial',
    'Microsoft.Web.Infrastructure',
    'Modernizr',
    'Microsoft.Bcl',
    'Moq',
    'Microsoft.Owin',
    'bootstrap',
    'Microsoft.jQuery.Unobtrusive.Ajax',
    'Microsoft.Owin.Host.SystemWeb',
    'Microsoft.Owin.Security',
    'NuGet.CommandLine',
    'NUnit',
    'Microsoft.AspNet.WebApi.OData',
    'log4net',
    'knockoutjs',
    'Microsoft.WindowsAzure.ConfigurationManager',
    'Owin',
    'AutoMapper',
    'NuGet.Build',
    'Microsoft.AspNet.Identity.Core',
    'Microsoft.Owin.Security.OAuth',
    'Microsoft.Owin.Security.Cookies',
    'Respond',
    'Unity',
    'Microsoft.AspNet.WebPages.WebData',
    'Microsoft.Data.Services.Client',
    'Microsoft.AspNet.Identity.Owin',
    'Microsoft.AspNet.Mvc.FixedDisplayModes',
    'Microsoft.AspNet.WebPages.Data',
    'nlog',
    'microsoft.aspnet.signalr'#>
    )

$projectSystemNames = (
    'New-MvcApplication',
    'New-ConsoleApplication',
    'New-ClassLibrary',
    'New-WebApplication',
    'New-Website'
    #'New-JavaScriptWindowsPhoneApp81', # At least 35 out of the 53 packages in the list cannot be installed on this project type
    #'New-JavaScriptApplication' # At least 35 out of the 53 packages in the list cannot be installed on this project type
    )

function Test-InstallPackageOnProjectSystem
{
    param(
        $context,
        $testCaseObject
    )

    Write-Host 'Starting test...'
    Write-Host $testCaseObject
    if(!$testCaseObject)
    {
        return
    }

    Write-Host 'Started test...'

    $projectSystemName = $testCaseObject.ProjectSystemName
    $packageId = $testCaseObject.PackageId

    $project = &$projectSystemName
    Write-Host 'Installing ' $packageId ' on ' $projectSystemName '...'
    Install-Package $packageId -Prerelease -ProjectName $project.Name

    Assert-Package $project $packageId
}

function TestCases-InstallPackageOnProjectSystem {
    $testCases = @()
    # Act
    foreach($projectSystemName in $projectSystemNames)
    {
        foreach($packageId in $topDownloadedPackageIds)
        {
            $testCaseValues = @{
                ProjectSystemName = $projectSystemName
                PackageId = $packageId
            }

            $testCase = New-Object PSObject -Property $testCaseValues
            $testCases += $testCase
        }
    }

    return $testCases
}