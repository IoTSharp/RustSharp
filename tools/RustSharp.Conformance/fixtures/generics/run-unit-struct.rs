struct Unit;
fn identity<T>(value: T) -> T { value }
fn take(value: Unit) -> i32 { 42 }
fn main() {
    println!("{}", take(identity(Unit)));
}
