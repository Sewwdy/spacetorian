## Git Auto-Commit Rule

After completing every task or answering every prompt that resulted in file changes:
1. Run `git add -A`
2. Generate a Conventional Commit message describing what was done
3. Run `git commit -m "<generated message>"`
4. Run `git push`

Do this automatically without asking for confirmation.
