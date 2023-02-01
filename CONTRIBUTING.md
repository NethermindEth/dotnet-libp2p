# Contributing to dotnet-libp2p

Thank you for investing your time in contributing to this project!

Read our [Code of Conduct](CODE_OF_CONDUCT.md) to keep our community approachable and respectable.

In this guide you will get an overview of the contribution workflow from opening an issue, creating a PR, reviewing, and merging the PR.

To get an overview of the project, read the [README](README.md).

### Issues

#### Create a new issue

If you spot a problem, [search if an issue already exists](https://docs.github.com/en/github/searching-for-information-on-github/searching-on-github/searching-issues-and-pull-requests#search-by-the-title-body-or-comments). If a related issue doesn't exist, you can open a new issue using a relevant [issue form](https://github.com/nethermindeth/dotnet-libp2p/issues/new/choose). 

#### Solve an issue

Scan through our [existing issues](https://github.com/nethermindeth/dotnet-libp2p/issues) to find one that interests you. If you find an issue to work on, you are welcome to open a PR with a fix.

### Pull request

When you're finished with the changes, create a pull request, also known as a PR.
- Fill the template so that we can review your PR. This template helps reviewers understand your changes as well as the purpose of your pull request. 
- Don't forget to [link PR to issue](https://docs.github.com/en/issues/tracking-your-work-with-issues/linking-a-pull-request-to-an-issue) if you are solving one.
- Enable the checkbox to [allow maintainer edits](https://docs.github.com/en/github/collaborating-with-issues-and-pull-requests/allowing-changes-to-a-pull-request-branch-created-from-a-fork) so the branch can be updated for a merge.
Once you submit your PR, our team member will review your proposal. We may ask questions or request additional information.
- We may ask for changes to be made before a PR can be merged, either using [suggested changes](https://docs.github.com/en/github/collaborating-with-issues-and-pull-requests/incorporating-feedback-in-your-pull-request) or pull request comments.
- As you update your PR and apply changes, mark each conversation as [resolved](https://docs.github.com/en/github/collaborating-with-issues-and-pull-requests/commenting-on-a-pull-request#resolving-conversations).
- If you run into any merge issues, checkout this [git tutorial](https://github.com/skills/resolve-merge-conflicts) to help you resolve merge conflicts and other issues.

#### DOs and DON'Ts

- **DO** give priority to the current style of the project or file you're changing even if it diverges from the general guidelines.
- **DO** include tests when adding new features. When fixing bugs, start with adding a test that highlights how the current behavior is broken.
- **DO** especially follow our rules in the [Contributing](CODE_OF_CONDUCT.md#contributing) section of our Code of Conduct.

<!-- -->

- **DON'T** create a new file without the proper file header.
- **DON'T** fill the issues and PR descriptions vaguely. The elements in the templates are there for a good reason. Help the team.
- **DON'T** surprise us with big pull requests. Instead, file an issue and start a discussion so we can agree on a direction before you invest a large amount of time.

#### Branch naming

Branch names must follow `kebab-case` pattern. Follow the pattern `feature/<issue>-<title>` or `hotfix/<issue>-<title>` when it is possible and add issue reference if applicable. For example:

- `feature/1234-enhancement-impl`
- `hotfix/1234-bug-fix`

#### File headers

The following notice must be included as a header in all source files if possible.

```
// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT
```

The `//` should be replaced depending on the file. For example, for Linux shell scripts, it is `#`.
