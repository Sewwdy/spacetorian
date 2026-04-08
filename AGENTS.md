## Git Auto-Commit Rule

After completing every task or answering every prompt that resulted in file changes:
1. Run `git add -A`
2. Generate a Conventional Commit message describing what was done
3. Run `git commit -m "<generated message>"`
4. Run `git push`

Do this automatically without asking for confirmation.

## Build Verification Rule

When a task changes source code or project/build configuration and build validation is required:
1. Stop any running `Spacetorian.exe` or `Monitorian` process that may lock build outputs.
2. Run `& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" "C:\Projects\spacetorian\Source\Monitorian.sln" /t:Build /p:Configuration=Release /p:Platform="Any CPU"`.
3. Report success/failure and key build errors in the response.

Do this before finalizing the answer when build is required.
