fn main() { let escaped; { let value: i32 = 7; escaped = &value; } println!("{}", *escaped); }
