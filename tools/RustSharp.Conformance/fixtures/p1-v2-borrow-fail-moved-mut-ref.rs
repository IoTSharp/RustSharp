fn main() { let mut value: i32 = 1; let first = &mut value; let second = first; println!("{}", *first); println!("{}", *second); }
