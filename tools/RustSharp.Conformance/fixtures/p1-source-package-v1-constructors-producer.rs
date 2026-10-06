pub struct Pair { pub left: i32, pub right: bool }
pub struct Tuple(pub i32);
pub struct Unit;
pub fn read(value: Pair) -> i32 { value.left }
pub fn tuple(value: Tuple) -> i32 { value.0 }
pub fn unit(value: Unit) -> i32 { 42 }
fn main() {}
