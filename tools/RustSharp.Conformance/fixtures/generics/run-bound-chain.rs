trait Marker {}
impl Marker for i32 {}
impl Marker for bool {}
fn identity<T>(value: T) -> T { value }
fn marked<T: Marker>(value: T) -> T { identity(value) }
fn choose<T>(flag: bool, left: T, right: T) -> T {
    if flag { left } else { right }
}
fn main() {
    println!("{}", choose(false, marked(0), marked(42)));
    println!("{}", marked(true));
}
