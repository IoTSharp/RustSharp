fn main() { let mut value: i32 = 1; let r = &mut value; *r = 9; println!("{}", *r); println!("{}", value); }
