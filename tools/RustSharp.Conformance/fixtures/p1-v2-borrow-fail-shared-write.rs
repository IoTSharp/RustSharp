fn main() { let mut value: i32 = 1; let shared = &value; value = 2; println!("{}", *shared); }
