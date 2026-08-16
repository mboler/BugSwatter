# Finding case studies

These are real BugSwatter findings that were investigated and fixed. The examples preserve the defect shape but omit internal hostnames, endpoints, absolute paths, credentials, and private network details. The commercial example is deliberately unnamed and rewritten around generic components.

The point is not that a diff-scoped reviewer could never find these bugs. The point is that each finding depended on supporting context outside the most obvious changed line. Repository-aware clusters made that context available in the same review conversation.

## Sources

- [BugSwatter reviewing BugSwatter](bugswatter.md)
- [BugSwatter reviewing MVNC](mvnc.md)
- [BugSwatter reviewing a commercial-style security product](commercial-style-security-product.md)

Every case records the reported problem, a minimal version of the code shape, the unchanged context that mattered, and the validator disposition. A validator confirmation is still a lead for human review, not proof that the finding is correct.
