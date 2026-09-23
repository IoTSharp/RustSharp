fn main() { let mut value: i32 = 1; let parent = &mut value; *parent = 7; { let child = &*parent; println!("{}", *child); } *parent = 9; println!("{}", value); }
