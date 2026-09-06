fn unit() { return }
fn value() -> i32 { return 1 }
fn early(flag: bool) -> i32 { if flag { return 1; } return 2; }
