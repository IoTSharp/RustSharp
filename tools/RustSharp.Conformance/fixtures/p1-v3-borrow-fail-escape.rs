// frozen P1 fixture: borrow-fail-escape
fn main() { let escaped; { let value: i32 = 7; escaped = &value; } println!("{}", *escaped); }
