// frozen P1 fixture: borrow-mutable-reborrow-chain
fn main() { let mut value: i32 = 1; let parent = &mut value; { let child = &mut *parent; *child = 9; println!("{}", *child); } println!("{}", *parent); println!("{}", value); }
