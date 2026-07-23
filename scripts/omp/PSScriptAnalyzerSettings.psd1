# PSScriptAnalyzer settings for the repository script grind.
# Scope: security rules + Windows PowerShell 5.1 compatibility.
# This file is itself scanned by the grind and must stay PS5.1-parseable.
@{
    Severity    = @('Error', 'Warning')

    IncludeRules = @(
        # Security
        'PSAvoidUsingPlainTextForPassword',
        'PSAvoidUsingConvertToSecureStringWithPlainText',
        'PSAvoidUsingUsernameAndPasswordParams',
        'PSUsePSCredentialType',
        'PSAvoidUsingInvokeExpression',
        'PSAvoidUsingBrokenHashAlgorithms',
        # Windows PowerShell 5.1 compatibility
        'PSUseCompatibleSyntax',
        'PSUseCompatibleCommands'
    )

    Rules = @{
        PSUseCompatibleSyntax = @{
            Enable           = $true
            TargetedVersions = @('5.1')
        }

        PSUseCompatibleCommands = @{
            Enable         = $true
            # Pinned to the Windows PowerShell 5.1 desktop profile that ships
            # with PSScriptAnalyzer (compatibility_profiles directory).
            TargetProfiles = @(
                'win-8_x64_10.0.17763.0_5.1.17763.316_x64_4.0.30319.42000_framework'
            )
        }
    }
}
