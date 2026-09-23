fn main() { let mut value: i32 = 1; let first = &mut value; let second = &mut value; *second = 2; println!("{}", *first); }
