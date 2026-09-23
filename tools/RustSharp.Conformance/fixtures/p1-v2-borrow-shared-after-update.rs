fn main() { let mut value: i32 = 1; let first = &value; println!("{}", *first); value = 9; let second = &value; println!("{}", *second); }
