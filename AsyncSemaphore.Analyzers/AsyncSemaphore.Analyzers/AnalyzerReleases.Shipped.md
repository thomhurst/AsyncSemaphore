## Release 1.0

### New Rules

Rule ID | Category | Severity | Notes                                            
--------|----------|----------|--------------------------------------------------
SEM0001  | Usage    | Warning  | WaitAsync should be awaited.  
SEM0002  | Usage    | Warning  | The lock should be assigned to a variable. 
SEM0003  | Usage    | Warning  | The using keyword should be used to automatically release the lock. 
SEM0004  | Usage    | Warning  | The releaser's Dispose method should not be called explicitly. 